using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Factory.Contracts;
using Microsoft.Win32;

namespace Factory.Workbench;

public sealed record WorkerSetupRequest(string Id, string InstallationId, string ReleaseId, string Operation);
public sealed record WorkerSetupStatus(string RequestId, string InstallationId, string ReleaseId,
    int ProcessId, DateTimeOffset ObservedAt, string State, string Stage, string Detail);
public sealed record WorkerDistribution(string InstallationId, string Distribution, string Directory);
public sealed record WslCommandResult(int ExitCode, string Output, string Error);

public interface IWslCommands
{
    Task<WslCommandResult> RunAsync(string[] arguments, Stream? input, CancellationToken cancellationToken);
    bool OwnsDistribution(string name, string directory);
}

public sealed class WslCommands : IWslCommands
{
    public async Task<WslCommandResult> RunAsync(string[] arguments, Stream? input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("WSL could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var output = ReadAsync(process.StandardOutput, CancellationToken.None);
        var error = ReadAsync(process.StandardError, CancellationToken.None);
        try
        {
            if (input is not null)
                await input.CopyToAsync(process.StandardInput.BaseStream, deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("WSL did not complete its step within fifteen minutes.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(output, error);
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > 16 * 1024)
                text.Remove(0, text.Length - 16 * 1024);
        }
        return text.ToString().Replace("\0", "", StringComparison.Ordinal).Trim('\uFEFF', ' ', '\r', '\n');
    }

    public bool OwnsDistribution(string name, string directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WSL registration requires Windows.");
        using var registrations = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (registrations is null)
            return false;
        foreach (var id in registrations.GetSubKeyNames())
        {
            using var registration = registrations.OpenSubKey(id);
            if (registration?.GetValue("DistributionName") is not string registered ||
                !registered.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return registration.GetValue("BasePath") is string path &&
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace(@"\\?\", "", StringComparison.Ordinal)))
                    .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase) &&
                registration.GetValue("Version") is int version && version == 2;
        }
        return false;
    }
}

public sealed class WslWorkerEnvironment(string root, WorkbenchInstallation installation, IWslCommands commands)
{
    public const string ImageName = "ubuntu-base-24.04.5-base-amd64.tar.gz";
    public const string ImageSha256 = "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86";
    public const string LinuxUser = "worker";
    public const string Workspace = "/home/worker/work";
    public static string DistributionName(string installationId) => $"Factory-{Guid.Parse(installationId):N}";
    public static string WorkerExecutable(string release) => $"/opt/factory/releases/{release}/Factory.Workbench.Worker";
    public static string RequestPath(string root) => Path.Combine(root, "worker-setup-request.json");
    public static string StatusPath(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "worker-setup.json");
    public static string EnvironmentDirectory(string root) => Path.Combine(root, "linux");
    private string MarkerPath => Path.Combine(EnvironmentDirectory(root), "environment.json");
    private string DistributionDirectory => Path.Combine(EnvironmentDirectory(root), "distribution");
    private string Name => DistributionName(installation.InstallationId);

    public static bool IsReady(string root, WorkbenchConfiguration configuration, string release)
    {
        if (!File.Exists(StatusPath(root)))
            return false;
        var status = LocalState.Read<WorkerSetupStatus>(StatusPath(root));
        return status.State == "ready" && status.ProcessId == Environment.ProcessId &&
            status.InstallationId == configuration.InstallationId && status.ReleaseId == release &&
            configuration.Distribution == DistributionName(configuration.InstallationId) &&
            configuration.LinuxUser == LinuxUser && configuration.LinuxWorkspaceRoot == Workspace &&
            configuration.Development?.WorkerExecutable == WorkerExecutable(release);
    }

    public async Task ProcessAsync(WorkerSetupRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.Id, "D", out _) || request.InstallationId != installation.InstallationId ||
            request.ReleaseId != installation.ActiveRelease || request.Operation is not ("prepare" or "check" or "remove"))
            throw new InvalidDataException("Worker setup request does not match the selected installation.");
        var stage = "service-identity-wsl";
        try
        {
            Report("working", stage, "Checking WSL under the workbench service identity.");
            await Run(["--status"], cancellationToken);
            ValidateDirectory(EnvironmentDirectory(root));
            if (request.Operation == "remove")
            {
                stage = "remove-owned-distribution";
                ValidateMarker();
                if ((await List(false, cancellationToken)).Contains(Name, StringComparer.OrdinalIgnoreCase))
                {
                    RequireRegistration();
                    await Run(["--unregister", Name], cancellationToken);
                }
                if ((await List(false, cancellationToken)).Contains(Name, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("WSL still reports the owned distribution after removal.");
                if (Directory.Exists(DistributionDirectory))
                {
                    if (Directory.EnumerateFileSystemEntries(DistributionDirectory).Any())
                        throw new IOException("WSL unregistered the distribution but left its storage. Preserve it for recovery.");
                    Directory.Delete(DistributionDirectory);
                }
                File.Delete(MarkerPath);
                Report("removed", stage, "Factory-owned Linux distribution removed.");
                return;
            }
            if (request.Operation == "prepare")
            {
                stage = "register-owned-distribution";
                Report("working", stage, "Registering the dedicated Factory distribution as the service identity.");
                if (!File.Exists(MarkerPath))
                {
                    if ((await List(false, cancellationToken)).Contains(Name, StringComparer.OrdinalIgnoreCase) ||
                        Directory.Exists(DistributionDirectory))
                        throw new IOException("The Factory distribution name or storage is already occupied without its ownership record.");
                    LocalState.Write(MarkerPath, new WorkerDistribution(installation.InstallationId, Name, DistributionDirectory));
                }
                ValidateMarker();
                if (!(await List(false, cancellationToken)).Contains(Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(DistributionDirectory))
                        throw new IOException("An incomplete WSL import left storage. Setup will not overwrite it.");
                    var image = Path.Combine(Path.GetDirectoryName(installation.Executable())!, "worker", ImageName);
                    await using var stream = File.OpenRead(image);
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                        .Equals(ImageSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The bundled Ubuntu image does not match its pinned checksum.");
                    await Run(["--import", Name, DistributionDirectory, image, "--version", "2"], cancellationToken);
                }
                RequireRegistration();
                stage = "linux-prerequisites";
                Report("working", stage, "Installing Git, certificates and Linux runtime dependencies. This can take several minutes.");
                await RunLinuxScriptAsync("root", Bootstrap, cancellationToken);
                stage = "install-worker";
                Report("working", stage, "Copying the packaged worker and Copilot runtime into Linux.");
                var target = $"/opt/factory/releases/{installation.ActiveRelease}";
                await RunLinuxScriptAsync("root", $"set -eu\nmkdir -p '{target}'\n", cancellationToken);
                await using var archive = new FileStream(Path.Combine(EnvironmentDirectory(root), "payload.tar"),
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
                await WritePayloadAsync(archive, Path.Combine(Path.GetDirectoryName(installation.Executable())!, "worker", "linux-x64"),
                    cancellationToken);
                archive.Position = 0;
                await Run(["--distribution", Name, "--user", "root", "--exec", "/bin/tar", "-xf", "-", "-C", target],
                    cancellationToken, archive);
                await TerminateAsync(cancellationToken);
            }
            stage = "worker-sdk-check";
            Report("working", stage, "Starting the worker and SDK as the unprivileged Linux user. No model call is made.");
            ValidateMarker();
            RequireRegistration();
            var result = await Run(["--distribution", Name, "--user", LinuxUser, "--exec",
                WorkerExecutable(installation.ActiveRelease), "check", "--workspace-root", Workspace], cancellationToken);
            using (var json = System.Text.Json.JsonDocument.Parse(result.Output))
                if (!json.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean() ||
                    !json.RootElement.TryGetProperty("sdkStarted", out var sdkStarted) || !sdkStarted.GetBoolean())
                    throw new IOException("The Linux worker did not confirm SDK readiness.");
            stage = "whole-distribution-stop";
            await TerminateAsync(cancellationToken);
            Report("ready", "complete", "Worker and SDK startup passed under the service identity; whole-distribution stop confirmed. Model access requires an authorized mission.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Report("failed", stage, $"{error.GetType().Name}: {error.Message}");
            if (request.Operation != "remove" && File.Exists(MarkerPath) && commands.OwnsDistribution(Name, DistributionDirectory))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await TerminateAsync(cleanup.Token); }
                catch (Exception cleanupError) when (cleanupError is IOException or OperationCanceledException)
                {
                    Report("failed", stage, $"{error.Message} Linux shutdown is also unconfirmed: {cleanupError.Message}");
                    throw new AggregateException("Worker preparation and Linux shutdown failed.", error, cleanupError);
                }
            }
            throw;
        }

        void Report(string state, string currentStage, string detail) =>
            LocalState.Write(StatusPath(root), new WorkerSetupStatus(request.Id, installation.InstallationId,
                installation.ActiveRelease, Environment.ProcessId, DateTimeOffset.UtcNow, state, currentStage,
                detail.Length > 4096 ? detail[^4096..] : detail));
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        ValidateMarker();
        RequireRegistration();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if ((await List(true, deadline.Token)).Contains(Name, StringComparer.OrdinalIgnoreCase))
            await Run(["--terminate", Name], deadline.Token);
        while ((await List(true, deadline.Token)).Contains(Name, StringComparer.OrdinalIgnoreCase))
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
    }

    private void ValidateMarker()
    {
        ValidateDirectory(EnvironmentDirectory(root));
        var marker = LocalState.Read<WorkerDistribution>(MarkerPath);
        if (marker.InstallationId != installation.InstallationId || marker.Distribution != Name ||
            marker.Directory != DistributionDirectory)
            throw new InvalidDataException("Linux environment ownership does not match this installation.");
        if (Directory.Exists(DistributionDirectory))
            ValidateDirectory(DistributionDirectory);
    }

    private void RequireRegistration()
    {
        if (!commands.OwnsDistribution(Name, DistributionDirectory))
            throw new IOException("WSL registration is not version 2 at the installation-owned path under this service identity.");
    }

    public static void ValidateDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Linux environment must be an existing installation-owned directory, not a link.");
    }

    private async Task<string[]> List(bool running, CancellationToken cancellationToken)
    {
        var result = await Run(running ? ["--list", "--quiet", "--running"] : ["--list", "--quiet"], cancellationToken);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<WslCommandResult> Run(string[] arguments, CancellationToken cancellationToken, Stream? input = null)
    {
        var result = await commands.RunAsync(arguments, input, cancellationToken);
        if (result.ExitCode != 0)
            throw new IOException($"WSL {arguments[0]} failed ({result.ExitCode}). {result.Error} {result.Output}");
        return result;
    }

    internal Task<WslCommandResult> RunLinuxScriptAsync(string user, string script, CancellationToken cancellationToken) =>
        Run(["--distribution", Name, "--user", user, "--exec", "/bin/sh", "-eu", "-c",
            script.ReplaceLineEndings("\n")], cancellationToken);

    private static async Task WritePayloadAsync(Stream output, string directory, CancellationToken cancellationToken)
    {
        using var writer = new TarWriter(output, leaveOpen: true);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Worker payload cannot contain links.");
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            await using var content = File.OpenRead(path);
            var header = new byte[4];
            var read = await content.ReadAsync(header, cancellationToken);
            content.Position = 0;
            var executable = read == 4 && header.AsSpan().SequenceEqual(new byte[] { 127, 69, 76, 70 });
            var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
            {
                DataStream = content,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                    (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0)
            };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }
    }

    private const string Bootstrap = """
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y --no-install-recommends ca-certificates git curl util-linux procps libicu74 libssl3t64 libstdc++6 zlib1g libatomic1
        if ! id worker >/dev/null 2>&1; then useradd --create-home --shell /bin/bash worker; fi
        test "$(id -u worker)" -ne 0
        install -d -o worker -g worker -m 700 /home/worker/work
        mkdir -p /opt/factory/releases
        printf '[automount]\nenabled=false\n[interop]\nenabled=false\nappendWindowsPath=false\n[user]\ndefault=worker\n' > /etc/wsl.conf
        """;
}
