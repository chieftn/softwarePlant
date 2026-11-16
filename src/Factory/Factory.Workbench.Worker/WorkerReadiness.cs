using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitHub.Copilot;

namespace Factory.Workbench.Worker;

internal static class WorkerReadiness
{
    internal static async Task CheckAsync(string workspace, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || GetUserId() == 0)
            throw new InvalidOperationException("The worker readiness check requires an unprivileged Linux user.");
        var root = Path.GetFullPath(workspace);
        if (!root.StartsWith('/') || root == "/" || !MissionPermissionPolicy.Within(root, root))
            throw new InvalidDataException("Worker workspace must be an absolute non-root path without links.");
        var probe = Path.Combine(root, $".readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probe);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(probe, "write-check"), "Factory readiness", cancellationToken);
            using var git = Process.Start(new ProcessStartInfo("/usr/bin/git")
            {
                ArgumentList = { "--version" }, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            }) ?? throw new IOException("Git did not start.");
            await git.WaitForExitAsync(cancellationToken);
            if (git.ExitCode != 0)
                throw new IOException("Git readiness check failed.");
            await using var sdk = new CopilotClient(new CopilotClientOptions
            {
                WorkingDirectory = probe, BaseDirectory = Path.Combine(probe, "sdk"),
                UseLoggedInUser = false,
                Environment = new Dictionary<string, string>
                {
                    ["COPILOT_TELEMETRY_DISABLED"] = "true",
                    ["COPILOT_GITHUB_TELEMETRY_DISABLED"] = "true"
                }
            });
            await sdk.StartAsync(cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new { ready = true, sdkStarted = true, modelAccessVerified = false }));
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
