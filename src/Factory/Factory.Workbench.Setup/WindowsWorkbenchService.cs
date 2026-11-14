using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Factory.Workbench.Setup;

[SupportedOSPlatform("windows")]
public sealed class WindowsWorkbenchService : IWorkbenchService
{
    public async Task ConfigureAsync(string stateDirectory, WorkbenchInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MachineCertificates.ProtectStateDirectory(stateDirectory);
        var exists = Exists(installation);
        var executable = installation.Executable(installation.PendingRelease);
        var command = $"\"{executable}\" service --root \"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory))}\"";
        await Sc(exists ? "config" : "create", installation.ServiceName,
            "binPath=", command, "start=", "auto", "obj=", installation.ServiceAccount,
            "DisplayName=", DisplayName(installation));
        ConfigureRecovery(installation.ServiceName);
        await Sc("failureflag", installation.ServiceName, "1");
        MachineCertificates.GrantServiceStateAccess(stateDirectory, installation);
        await Run("icacls.exe", [installation.InstallDirectory, "/grant:r",
            $"{installation.ServiceAccount}:(OI)(CI)RX", "/T"]);
        MachineCertificates.GrantServiceAccess(stateDirectory, installation);

        Task Sc(params string[] arguments) => Run("sc.exe", arguments);

        async Task Run(string executableName, string[] arguments)
        {
            await RunCommand(executableName, arguments, cancellationToken);
        }
    }

    public async Task StartAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Exists(installation))
            throw new InvalidDataException("Workbench service is missing. Run recover.");
        using var controller = new ServiceController(installation.ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
            controller.Start();
        await WaitAsync(controller, ServiceControllerStatus.Running, cancellationToken);
    }

    public async Task StopAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Exists(installation))
            return;
        using var controller = new ServiceController(installation.ServiceName);
        if (controller.Status == ServiceControllerStatus.StartPending)
            await WaitAsync(controller, ServiceControllerStatus.Running, cancellationToken);
        if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            controller.Stop();
        await WaitAsync(controller, ServiceControllerStatus.Stopped, cancellationToken);
    }

    public async Task RemoveAsync(WorkbenchInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exists(installation))
        {
            await RunCommand("sc.exe", ["delete", installation.ServiceName], cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                while (Exists(installation))
                    await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Windows has not released the deleted service. Close service-management handles and recover.");
            }
        }
    }

    public bool IsRunning(WorkbenchInstallation installation)
    {
        if (!Exists(installation))
            return false;
        using var controller = new ServiceController(installation.ServiceName);
        return controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }

    public int RunningProcessId(WorkbenchInstallation installation)
    {
        if (!Exists(installation))
            return 0;
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var handle = OpenService(manager, installation.ServiceName, 4);
        if (handle.IsInvalid || !QueryServiceStatusEx(handle, 0, out var status,
            Marshal.SizeOf<ServiceProcessStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.CurrentState == 4 ? checked((int)status.ProcessId) : 0;
    }

    private static bool Exists(WorkbenchInstallation installation)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{installation.ServiceName}");
        if (key is null)
            return false;
        if (key.GetValue("DisplayName") is not string displayName || displayName != DisplayName(installation) ||
            key.GetValue("ObjectName") is not string account ||
            !account.Equals(installation.ServiceAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Service name is occupied by a different installation or account; refusing to modify it.");
        return true;
    }

    private static string DisplayName(WorkbenchInstallation installation) =>
        $"Factory Workbench ({installation.InstallationId})";

    private static async Task WaitAsync(ServiceController controller, ServiceControllerStatus target,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                controller.Refresh();
                if (controller.Status == target)
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Service {controller.ServiceName} did not reach {target} within 45 seconds.");
        }
    }

    private static async Task RunCommand(string name, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new Win32Exception($"Could not start {name}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new IOException($"{name} failed with exit {process.ExitCode}: {output.Trim()} {error.Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"{name} timed out.");
        }
    }

    private static void ConfigureRecovery(string serviceName)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, serviceName, 2 | 16);
        if (service.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ServiceAction[] actions = [new(1, 10_000), new(1, 30_000), new(1, 60_000), new(0, 0)];
        var size = Marshal.SizeOf<ServiceAction>();
        var buffer = Marshal.AllocHGlobal(size * actions.Length);
        try
        {
            for (var index = 0; index < actions.Length; index++)
                Marshal.StructureToPtr(actions[index], buffer + index * size, false);
            var failure = new ServiceFailureActions
            {
                ResetPeriod = 86_400, ActionCount = (uint)actions.Length, Actions = buffer
            };
            if (!ChangeServiceConfig2(service, 2, ref failure))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ServiceAction(int type, uint delay)
    {
        public readonly int Type = type;
        public readonly uint Delay = delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public nint RebootMessage;
        public nint Command;
        public uint ActionCount;
        public nint Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceProcessStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(NativeServiceHandle service, int level,
        out ServiceProcessStatus status, int size, out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern NativeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern NativeServiceHandle OpenService(NativeServiceHandle manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(NativeServiceHandle service, uint level, ref ServiceFailureActions information);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint service);

    private sealed class NativeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeServiceHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
}
