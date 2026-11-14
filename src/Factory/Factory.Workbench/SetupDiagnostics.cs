using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;

namespace Factory.Workbench;

public sealed record SetupDiagnostic(DateTimeOffset At, string Session, string Version, string Stage,
    string Outcome, string? ErrorType = null, int? ErrorCode = null, string? Stack = null,
    int? HttpStatus = null, string? CorrelationId = null, string? EnrollmentId = null,
    string? AuthenticationError = null);

public sealed class SetupDiagnostics
{
    private static readonly Regex LogName = new(@"\Asetup-[a-f0-9]{32}\.jsonl\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly string version = typeof(SetupDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";
    private bool suspended;
    private bool published;
    public string DirectoryPath { get; }
    public string FilePath { get; }

    public SetupDiagnostics(string root)
    {
        DirectoryPath = Path.Combine(root, "logs");
        Directory.CreateDirectory(DirectoryPath);
        ValidateDirectory(DirectoryPath);
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(DirectoryPath);
            var acl = directory.GetAccessControl();
            SetLogAccess(acl, directory: true);
            directory.SetAccessControl(acl);
        }
        FilePath = Path.Combine(DirectoryPath, $"setup-{session}.jsonl");
        foreach (var old in Directory.EnumerateFiles(DirectoryPath).OrderByDescending(File.GetLastWriteTimeUtc).Skip(31))
            File.Delete(old);
    }

    public void Record(string stage, string outcome, Exception? error = null, string? enrollmentId = null)
    {
        if (suspended)
            return;
        Directory.CreateDirectory(DirectoryPath);
        ValidateDirectory(DirectoryPath);
        if (!Regex.IsMatch(stage, @"\A[a-z-]{1,80}\z") || !Regex.IsMatch(outcome, @"\A[a-z-]{1,80}\z"))
            throw new ArgumentException("Diagnostic stages/outcomes must be fixed identifiers, not user input.");
        if (enrollmentId is not null && !Guid.TryParseExact(enrollmentId, "D", out _))
            throw new ArgumentException("A diagnostic enrollment ID must be a UUID.");
        var stack = error?.StackTrace is { } trace
            ? Regex.Replace(trace, @" in .+?:line \d+", " [source location omitted]") : null;
        var entry = new SetupDiagnostic(DateTimeOffset.UtcNow, session, version, stage, outcome,
            error?.GetType().FullName, error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : error?.HResult, stack,
            error is PlantConnectionException request ? (int)request.Status :
                error is MsalServiceException identityService ? identityService.StatusCode : null,
            SafeCorrelation(error is MsalException authentication ? authentication.CorrelationId :
                (error as PlantConnectionException)?.CorrelationId), enrollmentId,
            error is MsalException { ErrorCode: { } code } && Regex.IsMatch(code, @"\A[A-Za-z0-9_-]{1,128}\z") ? code : null);
        var line = JsonSerializer.Serialize(entry, Json);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= 256 * 1024)
            throw new IOException("Setup diagnostic log reached its size limit. Save diagnostics and restart setup.");
        File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
        if (!published && OperatingSystem.IsWindows())
        {
            var file = new FileInfo(FilePath);
            var acl = file.GetAccessControl();
            SetLogAccess(acl, directory: false);
            file.SetAccessControl(acl);
            published = true;
        }
        Console.WriteLine("FACTORY_DIAGNOSTIC " + line);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetLogAccess(FileSystemSecurity acl, bool directory)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        using var identity = WindowsIdentity.GetCurrent();
        var creator = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        acl.SetAccessRuleProtection(true, false);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            Modify(AccessControlModification.RemoveSpecific, rule);
        foreach (var principal in new[] { administrators, system })
            Modify(AccessControlModification.Add, new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
        if (creator is not null && creator == identity.User && creator != administrators && creator != system)
            Modify(AccessControlModification.Add,
                new FileSystemAccessRule(creator, FileSystemRights.FullControl, AccessControlType.Allow));
        Modify(AccessControlModification.Add,
            new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                directory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read, AccessControlType.Allow));

        void Modify(AccessControlModification modification, FileSystemAccessRule rule)
        {
            if (!acl.ModifyAccessRule(modification, rule, out _))
                throw new InvalidOperationException("Diagnostic access rules could not be established.");
        }
    }

    public bool TryRecordFailure(string stage, string outcome, Exception? error = null)
    {
        try { Record(stage, outcome, error); return true; }
        catch (Exception loggingError) when (loggingError is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"FACTORY_DIAGNOSTICS_UNAVAILABLE {loggingError.GetType().Name} {loggingError.HResult}");
            return false;
        }
    }

    public void RunStage(string stage, Action operation)
    {
        Record(stage, "started");
        try { operation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or
            System.Security.SecurityException)
        {
            TryRecordFailure(stage, "failed", error);
            throw;
        }
        Record(stage, "completed");
    }

    public void Suspend() => suspended = true;

    public void Export(string destination, string root, string? msiLog = null)
    {
        var full = Path.GetFullPath(destination);
        RejectNested(full, root);
        if (File.Exists(Path.Combine(root, "installation.json")))
            RejectNested(full, LocalState.ReadInstallation(root).InstallDirectory);
        ValidateDirectory(DirectoryPath);
        using var output = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var path in Directory.EnumerateFiles(DirectoryPath))
        {
            if (new FileInfo(path).Length > 256 * 1024)
                throw new InvalidDataException("A setup diagnostic file exceeds the supported size.");
            using var reader = File.OpenText(path);
            using var writer = new StreamWriter(archive.CreateEntry(Path.GetFileName(path)).Open());
            while (reader.ReadLine() is { } line)
            {
                var diagnostic = JsonSerializer.Deserialize<SetupDiagnostic>(line, Json)
                    ?? throw new InvalidDataException("Invalid setup diagnostic entry.");
                writer.WriteLine(JsonSerializer.Serialize(diagnostic, Json));
            }
        }
        if (msiLog is not null)
        {
            if (!Path.IsPathFullyQualified(msiLog) || !msiLog.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(msiLog) || (File.GetAttributes(msiLog) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Windows Installer log is unavailable or not a regular log file.");
            using var stream = new FileStream(msiLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            using var writer = new StreamWriter(archive.CreateEntry("windows-installer.log").Open());
            var remaining = 20 * 1024 * 1024;
            while (remaining > 0 && reader.ReadLine() is { } line)
            {
                remaining -= line.Length;
                writer.WriteLine(Redact(line));
            }
            if (remaining <= 0)
                writer.WriteLine("[MSI log truncated at 20 MiB]");
        }
        using var summary = new StreamWriter(archive.CreateEntry("summary.json").Open());
        var installation = File.Exists(Path.Combine(root, "installation.json")) ? LocalState.ReadInstallation(root) : null;
        summary.Write(JsonSerializer.Serialize(new
        {
            collectedAt = DateTimeOffset.UtcNow, installation?.InstallationId, installation?.ActiveRelease,
            installation?.State, enrollmentId = File.Exists(Path.Combine(root, "enrollment.json"))
                ? BindingStore.ReadJournal(root).OperationId : null,
            note = "No configuration, certificate, private key, user-token cache or raw runtime exception log is included."
        }, Json));
    }

    public static bool IsOwnedLog(string name) => LogName.IsMatch(name);
    private static string? SafeCorrelation(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or ':')
            ? value : null;

    public static void ValidateDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(directory).Any(path =>
                !IsOwnedLog(Path.GetFileName(path)) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
            throw new InvalidDataException("Setup logs must contain only regular Factory diagnostic files.");
    }

    private static void RejectNested(string destination, string owned)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owned));
        if (destination.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save diagnostics outside installation-owned directories so uninstall will not remove the export.");
    }

    private static string Redact(string line)
    {
        line = Regex.Replace(line, @"(?i)(Bearer\s+|(?:access_token|refresh_token|id_token|client_secret|password)\s*[=:]\s*)[^\s;,""']+",
            "$1[redacted]", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(line, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            "[redacted token]", RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
