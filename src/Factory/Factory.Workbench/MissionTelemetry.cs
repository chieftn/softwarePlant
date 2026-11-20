using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Workbench;

public sealed record MissionTelemetryError(string Type, string Message, int HResult, string? Code = null,
    int? HttpStatus = null, string? CorrelationId = null, int? RpcCode = null, int? ExitCode = null, string? Stack = null);

public sealed record MissionTelemetryEntry(string SchemaVersion, string MissionId, string MissionDigest,
    string InstanceId, string Source, long Sequence, DateTimeOffset At, string Stage, string Outcome,
    int Iteration, long DurationMilliseconds, MissionTelemetryError[] Errors, string? Model = null, string? ReleaseId = null);

public sealed record WorkerOperationFailure(string Code, string Message, int? HttpStatus = null, string? CorrelationId = null);

public sealed class MissionOperationException(string stage, string message, Exception? inner = null,
    string? code = null, int? httpStatus = null, string? correlationId = null, int? rpcCode = null,
    int? exitCode = null) : IOException(message, inner)
{
    public string Stage { get; } = stage;
    public string? Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
    public string? CorrelationId { get; } = correlationId;
    public int? RpcCode { get; } = rpcCode;
    public int? ExitCode { get; } = exitCode;
}

public sealed class MissionTelemetry
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions JsonLines = new(ConfigurationStore.Json) { WriteIndented = false };
    private readonly string missionId;
    private readonly string digest;
    private readonly string instanceId;
    private readonly string source;
    private readonly Action<MissionTelemetryEntry>? publish;
    private readonly string? model;
    private readonly string? releaseId;
    private long sequence;
    private int iteration;
    public string FilePath { get; }
    public string Activity { get; private set; } = "Worker starting.";
    public static string DirectoryPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "mission-telemetry");

    public MissionTelemetry(string filePath, string missionId, string digest, string instanceId, string source,
        Action<MissionTelemetryEntry>? publish = null, string? model = null, string? releaseId = null)
    {
        if (!Guid.TryParseExact(missionId, "D", out _) || !Guid.TryParseExact(instanceId, "D", out _) ||
            digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) || source is not ("worker" or "host"))
            throw new InvalidDataException("Telemetry must identify an owned mission and worker instance.");
        FilePath = Path.GetFullPath(filePath);
        this.missionId = missionId;
        this.digest = digest;
        this.instanceId = instanceId;
        this.source = source;
        this.publish = publish;
        this.model = model;
        this.releaseId = releaseId;
    }

    public void Record(string stage, string outcome, int iteration = 0, long durationMilliseconds = 0, Exception? error = null)
    {
        lock (gate)
        {
            if (iteration > 0)
                this.iteration = iteration;
            var entry = new MissionTelemetryEntry("1", missionId, digest, instanceId, source, ++sequence,
                DateTimeOffset.UtcNow, stage, outcome, iteration == 0 ? this.iteration : iteration, durationMilliseconds, Project(error), model, releaseId);
            Append(entry);
            publish?.Invoke(entry);
        }
    }

    public bool TryRecordFailure(string stage, Exception error, int iteration = 0, long durationMilliseconds = 0)
    {
        try { Record(stage, "failed", iteration, durationMilliseconds, error); return true; }
        catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"MISSION_TELEMETRY_UNAVAILABLE {loggingError.GetType().Name}: {Safe(loggingError.Message)}");
            return false;
        }
    }

    public void Append(MissionTelemetryEntry entry)
    {
        lock (gate)
        {
            Validate(entry, missionId, digest);
            if (entry.InstanceId != instanceId)
                throw new InvalidDataException("Telemetry belongs to another worker launch.");
            var safe = entry with { Errors = entry.Errors.Select(Sanitize).ToArray() };
            var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(safe, JsonLines) + "\n");
            RequireRegularPath(FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (file.Length + content.Length > MaximumBytes)
                throw new IOException("Mission telemetry reached its 2 MiB bound; execution must stop without losing ownership.");
            file.Write(content);
            file.Flush(flushToDisk: true);
            var first = safe.Errors.FirstOrDefault();
            Activity = $"{entry.Stage}: {entry.Outcome} (iteration {entry.Iteration})" +
                (first is null ? "" : $"; {first.Code ?? first.Type}: {first.Message[..Math.Min(120, first.Message.Length)]}");
            if (Activity.Length > 256)
                Activity = Activity[..256];
            iteration = Math.Max(iteration, entry.Iteration);
        }
    }

    public static MissionTelemetryError[] Project(Exception? error)
    {
        var errors = new List<MissionTelemetryError>();
        for (var current = error; current is not null && errors.Count < 4; current = current.InnerException)
        {
            var operation = current as MissionOperationException;
            var request = current as PlantConnectionException;
            errors.Add(new(current.GetType().FullName ?? current.GetType().Name, Safe(current.Message), current.HResult,
                operation?.Code ?? request?.Code, operation?.HttpStatus ?? (request is null ? null : (int)request.Status),
                operation?.CorrelationId ?? request?.CorrelationId, operation?.RpcCode, operation?.ExitCode,
                current.StackTrace is { } stack ? Safe(Regex.Replace(stack, @" in .+?:line \d+", " [source location omitted]")) : null));
        }
        return errors.ToArray();
    }

    public static void Validate(MissionTelemetryEntry entry, string missionId, string digest)
    {
        if (entry.SchemaVersion != "1" || entry.MissionId != missionId || entry.MissionDigest != digest ||
            !Guid.TryParseExact(entry.InstanceId, "D", out _) || entry.Source is not ("worker" or "host") ||
            entry.Sequence <= 0 || entry.Iteration < 0 || entry.DurationMilliseconds < 0 ||
            entry.Stage is null || !Regex.IsMatch(entry.Stage, @"\A[a-z][a-z0-9-]{0,79}\z") ||
            entry.Outcome is not ("started" or "completed" or "failed" or "denied" or "received") ||
            entry.Model is not null && (entry.Model.Length is < 1 or > 128 || entry.Model.Any(char.IsControl)) ||
            entry.ReleaseId is not null && !Regex.IsMatch(entry.ReleaseId, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z") ||
            entry.Errors is null || entry.Errors.Length > 4 || entry.Errors.Any(error =>
                error is null || error.Type is null || error.Type.Length is < 1 or > 256 ||
                error.Message is null || error.Message.Length > 2048 ||
                error.Stack is not null && error.Stack.Length > 2048 ||
                error.Code is not null && !Regex.IsMatch(error.Code, @"\A[A-Za-z0-9_-]{1,128}\z") ||
                error.CorrelationId is not null && !Regex.IsMatch(error.CorrelationId, @"\A[A-Za-z0-9:-]{1,128}\z") ||
                error.HttpStatus is < 100 or > 599))
            throw new InvalidDataException("Invalid or out-of-scope mission telemetry.");
    }

    public static MissionTelemetryEntry[] Parse(string text, string missionId, string digest)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            throw new InvalidDataException("Mission telemetry exceeds its byte bound.");
        var entries = new List<MissionTelemetryEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = JsonSerializer.Deserialize<MissionTelemetryEntry>(line, ConfigurationStore.Json)
                ?? throw new InvalidDataException("Mission telemetry entry is empty.");
            Validate(entry, missionId, digest);
            entries.Add(entry with { Errors = entry.Errors.Select(Sanitize).ToArray() });
            if (entries.Count > 4096)
                throw new InvalidDataException("Mission telemetry exceeds its entry bound.");
        }
        return entries.ToArray();
    }

    public static void SaveCollected(string root, string missionId, string digest, string text)
    {
        var entries = Parse(text, missionId, digest);
        var path = Path.Combine(DirectoryPath(root), $"retained-{missionId}.json");
        RequireRegularPath(path);
        LocalState.Write(path, entries, MaximumSnapshotBytes);
    }

    public static MissionTelemetryEntry[] ReadLocal(string root, string missionId, string digest, bool live = false)
    {
        var directory = DirectoryPath(root);
        if (!Directory.Exists(directory))
            return [];
        RequireRegularPath(Path.Combine(directory, "telemetry-validation"));
        var entries = new List<MissionTelemetryEntry>();
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, $@"\A(?:mission-{Regex.Escape(missionId)}-[a-f0-9-]{{36}}\.jsonl|retained-{Regex.Escape(missionId)}\.json)\z"))
                continue;
            RequireRegularPath(path);
            if (name.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bytes += stream.Length;
                if (stream.Length > MaximumBytes)
                    throw new InvalidDataException("Mission telemetry exceeds its export byte bound.");
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (live && !text.EndsWith('\n'))
                    text = text[..(text.LastIndexOf('\n') + 1)];
                entries.AddRange(Parse(text, missionId, digest));
            }
            else
            {
                bytes += new FileInfo(path).Length;
                var retained = LocalState.Read<MissionTelemetryEntry[]>(path, MaximumSnapshotBytes);
                foreach (var entry in retained)
                    Validate(entry, missionId, digest);
                entries.AddRange(retained.Select(entry => entry with { Errors = entry.Errors.Select(Sanitize).ToArray() }));
            }
            if (entries.Count > 8192 || bytes > 16 * 1024 * 1024)
                throw new InvalidDataException("Mission telemetry exceeds its combined export bound.");
        }
        return entries.DistinctBy(entry => (entry.Source, entry.InstanceId, entry.Sequence)).OrderBy(entry => entry.At).ToArray();
    }

    public static string Safe(string message)
    {
        var redacted = SetupDiagnostics.Redact(message);
        return redacted.Length <= 2048 ? redacted : redacted[..2048];
    }

    private static MissionTelemetryError Sanitize(MissionTelemetryError error) => error with
    {
        Message = Safe(error.Message),
        Stack = error.Stack is null ? null : Safe(Regex.Replace(error.Stack, @" in .+?:line \d+", " [source location omitted]"))
    };

    public static void RequireRegularPath(string path)
    {
        var entry = new FileInfo(path);
        if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Mission telemetry cannot traverse filesystem links.");
        for (var directory = entry.Directory; directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Mission telemetry directories cannot traverse filesystem links.");
    }
}
