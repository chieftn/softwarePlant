using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Workbench;

public sealed record WorkbenchRuntimeStatus(string SchemaVersion, string InstallationId, string ReleaseId,
    int ProcessId, DateTimeOffset ObservedAt, string State, string Reason)
{
    public bool PlantConnected { get; init; }
    public Factory.Contracts.WorkbenchConfigurationObservation? ConfigurationObservation { get; init; }
}

public sealed record WorkbenchInstallation(
    string SchemaVersion, string InstallationId, string InstallDirectory, string State,
    string ActiveRelease, string? PreviousRelease, string? PendingRelease,
    string[] OwnedReleases, bool RunRequested)
{
    [JsonIgnore]
    public string ServiceName => $"Factory.Workbench.{Guid.Parse(InstallationId):N}";

    [JsonIgnore]
    public string ServiceAccount => $@"NT SERVICE\{ServiceName}";

    public string Executable(string? release = null) =>
        Path.Combine(InstallDirectory, "releases", release ?? ActiveRelease, "Factory.Workbench.Host.exe");

    public void Validate()
    {
        if (SchemaVersion != "1" || !Guid.TryParseExact(InstallationId, "D", out var id) || id == Guid.Empty ||
            State is not ("installing" or "installed" or "updating" or "uninstalling" or "uninstalled") ||
            !Path.IsPathFullyQualified(InstallDirectory) ||
            !Path.GetFullPath(InstallDirectory).Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            InstallDirectory.TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetPathRoot(InstallDirectory)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid workbench installation state.");
        ValidateRelease(ActiveRelease);
        if (PreviousRelease is not null)
            ValidateRelease(PreviousRelease);
        if (PendingRelease is not null)
            ValidateRelease(PendingRelease);
        if (OwnedReleases is null || OwnedReleases.Length > 256 ||
            OwnedReleases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != OwnedReleases.Length)
            throw new InvalidDataException("Invalid owned-release inventory.");
        foreach (var release in OwnedReleases)
            ValidateRelease(release);
        if (State != "uninstalled" && !OwnedReleases.Contains(ActiveRelease, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected release is not owned by this installation.");
        if (State == "updating" && PendingRelease is null ||
            State != "updating" && PendingRelease is not null)
            throw new InvalidDataException("Invalid pending release selection.");
    }

    public static void ValidateRelease(string release)
    {
        if (string.IsNullOrEmpty(release) || release.Length > 80 ||
            !char.IsAsciiLetterOrDigit(release[0]) || release.EndsWith('.') ||
            !release.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            throw new InvalidDataException("Invalid release ID.");
    }
}

public static class LocalState
{
    public static string RuntimeDirectory(string root) => Path.Combine(root, "runtime");
    public static string RuntimeStatusPath(string root) => Path.Combine(RuntimeDirectory(root), "runtime.json");
    public static FileStream LockRuntime(string root) => Lock(RuntimeDirectory(root), "runtime.lock");

    public static WorkbenchInstallation ReadInstallation(string root)
    {
        var state = Read<WorkbenchInstallation>(Path.Combine(root, "installation.json"));
        state.Validate();
        return state;
    }

    public static void SaveInstallation(string root, WorkbenchInstallation state)
    {
        state.Validate();
        Write(Path.Combine(root, "installation.json"), state);
    }

    public static T Read<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 128 * 1024)
            throw new InvalidDataException($"Local state exceeds 128 KiB: {Path.GetFileName(path)}");
        return JsonSerializer.Deserialize<T>(stream, ConfigurationStore.Json)
            ?? throw new InvalidDataException($"Local state must contain an object: {Path.GetFileName(path)}");
    }

    public static void Write<T>(string path, T value)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ConfigurationStore.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static FileStream Lock(string root, string name)
    {
        var directory = new DirectoryInfo(root);
        var entry = new FileInfo(Path.Combine(root, name));
        if (directory.LinkTarget is not null || entry.LinkTarget is not null ||
            directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Maintenance/runtime locks must not traverse filesystem links.");
        Directory.CreateDirectory(root);
        try
        {
            return new FileStream(Path.Combine(root, name), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException($"Cannot acquire {name}; another host or maintenance operation may own this installation.", error);
        }
    }
}
