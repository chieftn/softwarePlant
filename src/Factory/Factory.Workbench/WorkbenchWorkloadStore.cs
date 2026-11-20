using System.Text.Json;
using Factory.Contracts;

namespace Factory.Workbench;

public static class WorkbenchWorkloadStore
{
    public static string PathFor(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "repository-workload.json");

    public static ResolvedRepositoryWorkload? Read(string root, WorkbenchBinding binding, WorkbenchLineConfiguration configuration)
    {
        var path = PathFor(root);
        if (!File.Exists(path))
            return null;
        var workload = LocalState.Read<ResolvedRepositoryWorkload>(path);
        workload.Validate();
        if (workload.Configuration.EnrollmentId != binding.EnrollmentId || workload.Configuration.WorkbenchId != binding.WorkbenchId)
            throw new InvalidDataException("Repository workload belongs to another workbench identity.");
        return configuration.ProductionLine is not null && WorkloadRequest.From(configuration).Matches(workload.Configuration) &&
            workload.Configuration.PlantId == configuration.PlantId ? workload : null;
    }

    public static void Save(string root, WorkbenchBinding binding, ResolvedRepositoryWorkload workload)
    {
        workload.Validate();
        var current = WorkbenchLineConfigurationStore.Read(root, binding)?.Configuration;
        if (current is null || current.ProductionLine is null ||
            !WorkloadRequest.From(current).Matches(workload.Configuration) ||
            current.PlantId != workload.Configuration.PlantId ||
            workload.Configuration.EnrollmentId != binding.EnrollmentId ||
            workload.Configuration.WorkbenchId != binding.WorkbenchId)
            throw new InvalidDataException("A late workload result cannot reactivate revoked configuration.");
        LocalState.Write(PathFor(root), workload);
    }

    public static void Revoke(string root)
    {
        var path = PathFor(root);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static async Task<WorkbenchLineState> ObserveAsync(string root, WorkbenchBinding binding,
        WorkbenchLineState state, PlantHttp plant, CancellationToken cancellationToken)
    {
        if (state.Configuration.ProductionLine is null)
            return state;
        var reason = "";
        try
        {
            var workload = Read(root, binding, state.Configuration);
            if (workload is null || workload.Validate().Verification.Commands.Length == 0 ||
                workload.Validate().Workflow.CompletionCriterion is null)
            {
                workload = await plant.SendAsync<ResolvedRepositoryWorkload>(HttpMethod.Post,
                    $"v1/enrollments/{binding.EnrollmentId}/workload", WorkloadRequest.From(state.Configuration),
                    null, cancellationToken);
                Save(root, binding, workload);
            }
            var definition = workload.Validate();
            reason = definition.Verification.Commands.Length == 0
                ? "Repository specs pinned. Project verification commands are missing; execution remains disabled."
                : definition.Workflow.CompletionCriterion != "pull_request_merged"
                    ? "Repository specs pinned. Declare workflow.completionCriterion as pull_request_merged before execution."
                : WorkbenchExecutionConfiguration.Resolve(ConfigurationStore.Read(root), state.Configuration).Development?.Enabled == true
                    ? "Repository specs pinned. Worker readiness and owned mission execution are reported separately."
                    : "Repository specs pinned. Configure the line, prepare the SDK worker and select Start line in Setup.";
        }
        catch (PlantConnectionException error) when (error.Code is "repository_connection_unconfigured" or
            "repository_not_permitted" or "repository_access_failed" or "repository_access_timeout" or
            "repository_response_invalid" or "workload_invalid" or "workload_missing")
        {
            reason = $"New repository specs blocked: {error.Code}. This configuration cannot authorize a new mission.";
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {reason} Correlation: {error.CorrelationId ?? "unavailable"}.");
        }
        catch (Exception error) when (error is JsonException or ContractValidationException)
        {
            throw new InvalidDataException("Plant returned an invalid pinned workload.", error);
        }
        var current = WorkbenchLineConfigurationStore.Read(root, binding);
        if (current is null || !WorkloadRequest.From(state.Configuration).Matches(current.Configuration))
            throw new InvalidDataException("Workload observation was superseded.");
        var observed = current with { Observation = current.Observation with { State = "blocked", Reason = reason } };
        LocalState.Write(WorkbenchLineConfigurationStore.PathFor(root), observed);
        return observed;
    }
}
