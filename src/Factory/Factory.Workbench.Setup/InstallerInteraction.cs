using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Workbench.Setup;

internal static class InstallerInteraction
{
    public static async Task OfferDiagnosticsAsync(string executable, string log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Installer diagnostics require Windows.");
        MachineCertificates.ValidateInstallerExecutable(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidDataException("The installed diagnostic interface is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas"
        };
        start.ArgumentList.Add("--removal-diagnostics");
        start.ArgumentList.Add("--msi-log");
        start.ArgumentList.Add(log);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the removal diagnostics interface.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new OperationCanceledException("Uninstall was cancelled before local removal.");
    }

    public static void ShowRemovalOutcome(string transaction, string log)
    {
        var id = Guid.Parse(transaction).ToString("D");
        var outcome = "unconfirmed";
        if (Path.IsPathFullyQualified(log) && File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                var match = Regex.Match(line, $@"FACTORY_RETIREMENT {Regex.Escape(id)} (confirmed|pending|attention|not-enrolled|unconfirmed)\b");
                if (match.Success)
                    outcome = match.Groups[1].Value;
            }
        }
        var detail = outcome switch
        {
            "confirmed" => "Factory confirmed remote retirement.",
            "pending" => "Factory accepted retirement and revoked access. Directory cleanup is still pending.",
            "attention" => "Factory revoked access, but an administrator must resolve directory cleanup.",
            "not-enrolled" => "No remote enrollment was recorded locally.",
            _ => "Remote retirement is UNCONFIRMED. An enrollment administrator should check Factory inventory."
        };
        MessageBoxW(IntPtr.Zero, $"Owned local workbench artifacts were removed.\n\n{detail}\n\nWindows Installer log:\n{log}",
            "Factory Workbench removal", 0x40);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
