using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;

namespace Factory.Workbench.Host;

public static class WorkbenchRuntime
{
    public static async Task RunAsync(string root, string release, bool diagnostic, CancellationToken cancellationToken, ILogger? logger = null)
    {
        using var ownership = LocalState.LockRuntime(root);
        var installation = LocalState.ReadInstallation(root);
        if (installation.State != "installed" || installation.ActiveRelease != release)
            throw new InvalidDataException("Release activation is incomplete or this is not the selected release. Run setup recover.");
        var configuration = ConfigurationStore.Read(root);
        if (configuration.InstallationId != installation.InstallationId)
            throw new InvalidDataException("Configuration and installed machine identities do not match.");
        if (!diagnostic && !installation.RunRequested)
            return;
        BindingStore.RequireEnrollmentAllowed(root);
        var status = new WorkbenchRuntimeStatus("1", installation.InstallationId, release, Environment.ProcessId,
            DateTimeOffset.UtcNow, "notReady", "Machine enrollment is incomplete. WSL is not started.");
        X509Certificate2? certificate = null;
        HttpClient? http = null;
        WorkbenchPlantConnection? connection = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindingStore.RequireEnrollmentAllowed(root);
                if (File.Exists(Path.Combine(root, "binding.json")))
                {
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException("Machine certificate authentication requires Windows.");
                    try
                    {
                        if (connection is null)
                        {
                            var binding = BindingStore.Read(root);
                            certificate?.Dispose();
                            certificate = MachineCertificates.Read(binding.CertificateThumbprint);
                            var tokens = new CertificateTokenSource(configuration, binding, certificate);
                            http ??= PlantHttp.Create(configuration.PlantServiceUrl);
                            connection = new(new PlantHttp(http, tokens.TokenAsync, tokens.RequestFreshToken), binding, release, root);
                        }
                        await connection.PublishAsync(cancellationToken);
                        status = status with { PlantConnected = true, Reason = connection.AssignedPlantId is null
                            ? "Enrolled and connected to Factory; unassigned. Assign this device through the website. Execution is unavailable."
                            : "Factory and assigned plant connected; presence is notReady. Execution and WSL are unavailable." };
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or MsalException or CryptographicException ||
                        error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        status = status with { PlantConnected = false, Reason = $"Plant connection unavailable: {error.GetType().Name}. Execution remains disabled. WSL is not started." };
                        if (logger is not null)
                            logger.LogWarning(error, "Plant connection unavailable. Reports are retained; execution remains disabled.");
                        else
                            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {status.Reason} {error.Message}");
                    }
                }
                LocalState.Write(LocalState.RuntimeStatusPath(root), status with { ObservedAt = DateTimeOffset.UtcNow });
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LocalState.Write(LocalState.RuntimeStatusPath(root), status with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                State = "stopped",
                PlantConnected = false,
                Reason = "Host stopped cooperatively; no worker execution was started."
            });
        }
        finally
        {
            certificate?.Dispose();
            http?.Dispose();
        }
    }
}
