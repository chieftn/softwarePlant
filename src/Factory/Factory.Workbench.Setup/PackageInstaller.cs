using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Workbench.Setup;

public sealed record WorkbenchPackage(string SchemaVersion, string ReleaseId, string Runtime,
    string SourceRevision, Dictionary<string, string> Files);

public static partial class PackageInstaller
{
    public static string Install(string packageDirectory, string root, bool allowUnsignedPackage)
    {
        if (!allowUnsignedPackage)
            throw new InvalidDataException(
                "Publisher signature verification is not implemented. Local development installation requires --allow-unsigned-package.");
        var package = Path.GetFullPath(packageDirectory);
        var manifest = ReadManifest(package);
        var source = Path.Combine(package, "host");
        var files = ValidateFiles(source, manifest);
        var releases = Path.Combine(Path.GetFullPath(root), "releases");
        var destination = Path.Combine(releases, manifest.ReleaseId);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Release already exists; choose a new release ID. Installed releases are not overwritten.");
        if (Path.GetFullPath(root).Equals(package, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(root).StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            package.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package and installation directories must not contain one another.");

        Directory.CreateDirectory(releases);
        var staging = Path.Combine(releases, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(staging, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.Source, target, overwrite: false);
                CheckDigest(target, file.Digest, file.Relative);
            }
            File.WriteAllText(Path.Combine(staging, "workbench-package.json"),
                JsonSerializer.Serialize(manifest, ConfigurationStore.Json));
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        return Path.Combine(destination, "Factory.Workbench.Host.exe");
    }

    public static WorkbenchPackage ReadManifest(string directory)
    {
        var manifest = LocalState.Read<WorkbenchPackage>(Path.Combine(directory, "workbench-package.json"));
        if (manifest.SchemaVersion != "1" || !ReleaseId().IsMatch(manifest.ReleaseId) ||
            manifest.ReleaseId.EndsWith('.') || manifest.Runtime != "win-x64" ||
            string.IsNullOrWhiteSpace(manifest.SourceRevision) || manifest.Files.Count == 0 ||
            !manifest.Files.ContainsKey("Factory.Workbench.Host.exe"))
            throw new InvalidDataException("Unsupported or incomplete Windows host package manifest.");

        return manifest;
    }

    public static void VerifyInstalled(string directory, string expectedRelease)
    {
        var manifest = ReadManifest(directory);
        if (manifest.ReleaseId != expectedRelease)
            throw new InvalidDataException("Installed release manifest does not match its directory.");
        foreach (var file in ValidateFiles(directory, manifest))
            CheckDigest(file.Source, file.Digest, file.Relative);
    }

    public static void VerifySameRelease(string package, string installed)
    {
        var source = ReadManifest(package);
        var target = ReadManifest(installed);
        if (source.ReleaseId != target.ReleaseId || source.Runtime != target.Runtime ||
            source.SourceRevision != target.SourceRevision || source.Files.Count != target.Files.Count ||
            source.Files.Any(file => !target.Files.TryGetValue(file.Key, out var digest) ||
                !file.Value.Equals(digest, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This release ID already names a different payload. Publish with a new release ID.");
        VerifyInstalled(installed, source.ReleaseId);
    }

    private static List<(string Source, string Relative, string Digest)> ValidateFiles(string source, WorkbenchPackage manifest)
    {
        var files = new List<(string Source, string Relative, string Digest)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, digest) in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Equals("workbench-package.json", StringComparison.OrdinalIgnoreCase) ||
                relative.Split('\\', '/').Any(part => part.Length == 0 || part is "." or ".." ||
                    part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':')) ||
                relative.Contains('/') || !names.Add(relative) || !Digest().IsMatch(digest))
                throw new InvalidDataException("Package contains an invalid or duplicate file path or digest.");
            var path = Path.Combine(source, relative);
            RejectLinks(source, relative);
            if (!File.Exists(path))
                throw new InvalidDataException($"Package file is missing: {relative}");
            files.Add((path, relative, digest));
        }

        return files;
    }

    private static void CheckDigest(string path, string digest, string relative)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package digest mismatch: {relative}");
    }

    private static void RejectLinks(string root, string relative)
    {
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package host directory must not be a link.");
        foreach (var part in relative.Split('\\'))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Package files and directories must not be links.");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseId();

    [GeneratedRegex(@"\A[A-Fa-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
}
