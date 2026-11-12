using System.Diagnostics;

namespace Workbench.Host;

public static class WslLaunch
{
    public static ProcessStartInfo Create(string distribution, string workerPath, string workspaceRoot, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(distribution) ||
            !workerPath.StartsWith('/') || !workspaceRoot.StartsWith('/'))
            throw new ArgumentException("Specify a WSL distribution and absolute Linux worker/workspace paths.");
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "--distribution", distribution, "--exec",
            "timeout", "--signal=TERM", "--kill-after=10s", $"{timeoutSeconds + 20}s",
            "dotnet", workerPath, "--workspace-root", workspaceRoot
        })
            start.ArgumentList.Add(argument);
        return start;
    }
}
