using System.Runtime.InteropServices;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

if (args.Length == 0 || args is ["--help"] or ["help"])
{
    Console.WriteLine("""
        Factory workbench runtime (Windows x64)
          service [--root <state-directory>]   Windows Service Control Manager entry point
          run [--root <state-directory>]       Foreground diagnostic host; Ctrl+C stops it

        Installation/configuration/maintenance belong to Factory.Workbench.Setup.
        No WSL, enrollment, plant connection, or worker execution is implemented.
        """);
    return 0;
}

try
{
    if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        throw new PlatformNotSupportedException("The workbench runtime requires Windows x64.");
    if (args[0] is not ("run" or "service") ||
        args.Length != 1 && (args.Length != 3 || args[1] != "--root" || args[2].StartsWith("--", StringComparison.Ordinal)))
        throw new InvalidDataException("Use run or service [--root <state-directory>].");
    var diagnostic = args[0] == "run";
    if (!diagnostic && !WindowsServiceHelpers.IsWindowsService())
        throw new InvalidOperationException("The service command must be launched by Windows Service Control Manager. Use run for diagnostics.");
    var root = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(args.Length == 3 ? args[2] : ConfigurationStore.DefaultRoot));
    var installation = LocalState.ReadInstallation(root);
    if (!Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(
        Path.GetDirectoryName(installation.Executable()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Launch the selected installed release, not a package copy or a previous release.");
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [], ContentRootPath = AppContext.BaseDirectory
    });
    if (!diagnostic)
        builder.Services.AddWindowsService(options => options.ServiceName = installation.ServiceName);
    builder.Services.AddHostedService(provider => new WorkbenchService(root, installation.ActiveRelease,
        diagnostic, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkbenchService>>(),
        provider.GetRequiredService<IHostApplicationLifetime>()));
    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
    JsonException or ContractValidationException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"Workbench runtime failed: {error.Message}");
    return 1;
}
