using System.Diagnostics;
using System.Text;

namespace Workbench.Contracts;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string directory, CommandSpec command, CancellationToken cancellationToken);
}

public sealed class CommandRunner : ICommandRunner
{
    private const int OutputLimit = 65536;

    public async Task<CommandResult> RunAsync(string directory, CommandSpec command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + command.Executable);
        process.StandardInput.Close();
        var stdout = CaptureAsync(process.StandardOutput);
        var stderr = CaptureAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        return new(process.ExitCode, stdout.Result.Text, stderr.Result.Text,
            stdout.Result.Truncated || stderr.Result.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader stream)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        var truncated = false;
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > OutputLimit)
            {
                text.Remove(0, text.Length - OutputLimit);
                truncated = true;
            }
        }
        return (text.ToString(), truncated);
    }
}
