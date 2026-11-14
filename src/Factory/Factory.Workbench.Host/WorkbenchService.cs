using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Factory.Workbench.Host;

internal sealed class WorkbenchService(string root, string release, bool diagnostic,
    ILogger<WorkbenchService> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Workbench service is starting. Certificate-authenticated plant connection is enabled for enrolled machines; execution is disabled.");
            await WorkbenchRuntime.RunAsync(root, release, diagnostic, stoppingToken, logger);
            lifetime.StopApplication();
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            try
            {
                logger.LogCritical(error, "Workbench runtime fault; claims remain disabled.");
                var detail = $"{DateTimeOffset.UtcNow:O} {error}{Environment.NewLine}";
                File.WriteAllText(Path.Combine(LocalState.RuntimeDirectory(root), "runtime-failure.log"),
                    detail.Length <= 32 * 1024 ? detail : detail[..(32 * 1024)] + "\n[truncated]");
            }
            finally
            {
                if (!diagnostic)
                    Environment.Exit(1);
            }
            throw;
        }
    }
}
