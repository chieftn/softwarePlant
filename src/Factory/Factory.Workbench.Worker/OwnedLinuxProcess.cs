using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;

namespace Factory.Workbench.Worker;

internal static class OwnedLinuxProcess
{
    [DllImport("libc", SetLastError = true)]
    private static extern int getpgrp();
    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    public static OwnedLinuxWorker Register(string runDirectory, string missionId, string instanceId, string nonce)
    {
        var process = new OwnedLinuxWorker(Environment.ProcessId, getpgrp(), instanceId, nonce, missionId);
        if (process.ProcessGroupId != process.ProcessId || process.ProcessId <= 1)
            throw new InvalidDataException("Worker must be launched in its own setsid process group.");
        LocalState.Write(Path.Combine(runDirectory, "owned-process.json"), process);
        return process;
    }

    public static async Task<bool> StopAsync(string root, string missionId, string instance, string nonce, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(missionId, "D", out _) || !Guid.TryParseExact(instance, "D", out _) ||
            nonce.Length != 64 || !nonce.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Owned process stop requires exact mission/instance/nonce.");
        var run = Path.Combine(Path.GetFullPath(root), missionId);
        if (!MissionPermissionPolicy.Within(root, run))
            throw new InvalidDataException("Owned process record cannot traverse links.");
        var owned = LocalState.Read<OwnedLinuxWorker>(Path.Combine(run, "owned-process.json"));
        if (owned.MissionId != missionId || owned.InstanceId != instance || owned.Nonce != nonce ||
            owned.ProcessId <= 1 || owned.ProcessGroupId != owned.ProcessId)
            throw new InvalidDataException("Stop request does not match the recorded owned process group.");
        var proc = $"/proc/{owned.ProcessId}/cmdline";
        if (!File.Exists(proc))
            return kill(-owned.ProcessGroupId, 0) != 0 && Marshal.GetLastPInvokeError() == 3;
        var arguments = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(proc, cancellationToken)).Split('\0');
        var instanceIndex = Array.IndexOf(arguments, "--instance");
        var nonceIndex = Array.IndexOf(arguments, "--nonce");
        if (instanceIndex < 0 || nonceIndex < 0 || arguments.ElementAtOrDefault(instanceIndex + 1) != instance ||
            arguments.ElementAtOrDefault(nonceIndex + 1) != nonce || getpgid(owned.ProcessId) != owned.ProcessGroupId)
            throw new InvalidDataException("Live process identity changed; refusing to signal an unverified group.");
        if (kill(-owned.ProcessGroupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("Owned Linux group termination could not be signalled.");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (kill(-owned.ProcessGroupId, 0) != 0 && Marshal.GetLastPInvokeError() == 3)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        return false;
    }
}
