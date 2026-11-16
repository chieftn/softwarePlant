using Factory.Contracts;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentOperations
{
    public async Task<ResolvedRepositoryWorkload> WorkloadAsync(string id, ApiCaller caller, WorkloadRequest request,
        IGitHubRepositoryReader repositories, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationAsync(id, caller, cancellationToken);
        if (!request.Matches(configuration))
            throw new ApiException(409, "configuration_changed", "The workload request was superseded or revoked.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ResolvedRepositoryWorkload resolved;
        try
        {
            resolved = await repositories.ResolveAsync(configuration, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(503, "repository_access_timeout", "Repository workload resolution exceeded its bounded deadline.");
        }
        resolved.Validate();
        var current = await ConfigurationAsync(id, caller, cancellationToken);
        if (!request.Matches(current) || !request.Matches(resolved.Configuration) ||
            resolved.Configuration.EnrollmentId != id || resolved.Configuration.WorkbenchId != current.WorkbenchId ||
            resolved.Configuration.PlantId != current.PlantId)
            throw new ApiException(409, "configuration_changed", "Configuration changed while the workload was resolving.");
        return resolved;
    }
}
