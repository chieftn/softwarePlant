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
        WorkbenchDevelopmentExecution? development = null;
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
                            var plant = new PlantHttp(http, tokens.TokenAsync, tokens.RequestFreshToken);
                            development = new(plant, binding, configuration, root);
                            await development.RecoverAsync(cancellationToken);
                            connection = new(plant, binding, release, root, development.ObserveAsync,
                                () => (development.State, development.Reason));
                        }
                        await connection.PublishAsync(cancellationToken);
                        await development!.TickAsync(cancellationToken);
                        status = status with
                        {
                            PlantConnected = true,
                            ConfigurationObservation = connection.ConfigurationObservation,
                            State = configuration.Development?.Enabled == true ? development.State : "notReady",
                            Reason = configuration.Development?.Enabled == true ? development.Reason :
                                connection.ConfigurationObservation?.Reason ?? "Factory connected; execution remains disabled."
                        };
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or MsalException or CryptographicException ||
                        error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        if (development is not null)
                            await development.StopAsync(CancellationToken.None);
                        status = status with
                        {
                            PlantConnected = false,
                            State = "notReady",
                            ConfigurationObservation = connection?.ConfigurationObservation,
                            Reason = $"Plant connection unavailable: {error.GetType().Name}. Owned execution stopped; unresolved journals are retained."
                        };
                        if (logger is not null)
                            logger.LogWarning(error, "Plant connection unavailable. Reports are retained; owned execution is stopped.");
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
            if (development is not null)
                await development.StopAsync(CancellationToken.None);
            LocalState.Write(LocalState.RuntimeStatusPath(root), status with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                State = "stopped",
                PlantConnected = false,
                Reason = "Host stopped; any owned Linux execution was terminated and journals retained."
            });
        }
        finally
        {
            try
            {
                if (development is not null)
                    await development.StopAsync(CancellationToken.None);
            }
            finally
            {
                certificate?.Dispose();
                http?.Dispose();
            }
        }
    }
}
