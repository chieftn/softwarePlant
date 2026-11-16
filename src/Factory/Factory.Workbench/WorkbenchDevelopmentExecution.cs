using System.Net;
using Factory.Contracts;

namespace Factory.Workbench;

public sealed class WorkbenchDevelopmentExecution(PlantHttp plant, WorkbenchBinding binding,
    WorkbenchConfiguration configuration, string root)
{
    private WslMissionWorker? worker;
    private WorkbenchLineConfiguration? current;
    public string State { get; private set; } = "notReady";
    public string Reason { get; private set; } = "Development execution is disabled.";

    public async Task ObserveAsync(WorkbenchLineConfiguration observed, CancellationToken cancellationToken)
    {
        current = observed;
        if (worker is not null && !worker.Matches(observed))
            await StopAsync(cancellationToken);
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (worker is null)
            await RecoverAsync(cancellationToken);
        if (configuration.Development?.Enabled != true || current?.ProductionLine is null)
        {
            State = "notReady";
            Reason = configuration.Development?.Enabled == true
                ? "No production line is assigned; no mission execution is permitted."
                : "Development execution is disabled.";
            return;
        }
        if (!configuration.AllowShellExecution || !configuration.AllowPublication)
        {
            State = "notReady";
            Reason = "Local policy must explicitly permit project verification and publication before claiming an issue.";
            return;
        }
        if (!WslWorkerEnvironment.IsReady(root, configuration, LocalState.ReadInstallation(root).ActiveRelease))
        {
            State = "notReady";
            Reason = "Prepare the SDK worker in Factory Workbench Setup. Service-identity worker/SDK readiness is required before claiming work.";
            return;
        }
        var source = current.ProductionLine.Definition;
        if (!configuration.AllowedRepositories.Contains(source.Source.Repository, StringComparer.OrdinalIgnoreCase) ||
            !configuration.AllowedRepositories.Contains(source.Tasks.Repository, StringComparer.OrdinalIgnoreCase))
        {
            Reason = "Assigned source/issue repository is outside the explicit local allowlist.";
            return;
        }
        if (worker is not null)
        {
            State = "busy";
            Reason = "Owned frozen mission active; SDK sessions close before human review waits.";
            if (!worker.Completion.IsCompleted)
                return;
            try { await worker.Completion; }
            catch (IOException)
            {
                Reason = "Worker stopped with an unresolved mission boundary; retained evidence requires reconciliation.";
            }
            finally { await StopAsync(CancellationToken.None); }
            return;
        }
        var workload = WorkbenchWorkloadStore.Read(root, binding, current);
        if (workload is null || workload.Validate().Verification.Commands.Length == 0)
        {
            Reason = "Resolved project verification commands are required before execution.";
            return;
        }
        var endpoint = $"v1/enrollments/{binding.EnrollmentId}/mission";
        try
        {
            DevelopmentMissionDelivery? delivery = null;
            try { delivery = await plant.ReadAsync<DevelopmentMissionDelivery>(endpoint, cancellationToken); }
            catch (PlantConnectionException error) when (error.Status == HttpStatusCode.NotFound && error.Code == "mission_not_found") { }
            var pendingPath = Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-claim.json");
            if (delivery is null || delivery.State == "approved" &&
                delivery.Mission.Issue.Number != configuration.Development.AssignedIssueNumber)
            {
                var request = File.Exists(pendingPath) ? LocalState.Read<DevelopmentClaimRequest>(pendingPath)
                    : new(Guid.NewGuid().ToString("D"), WorkloadRequest.From(current), workload.Digest,
                        configuration.Development.AssignedIssueNumber, configuration.Development.Model);
                LocalState.Write(pendingPath, request);
                delivery = await plant.SendAsync<DevelopmentMissionDelivery>(HttpMethod.Post, endpoint, request,
                    request.OperationId, cancellationToken);
                File.Delete(pendingPath);
            }

            if (delivery.State == "approved")
            {
                State = "idle";
                Reason = "Assigned issue approved. No automatic merge or repeat execution.";
                return;
            }
            if (delivery.State is "reserving" or "rejected" or "blocked" or "cancelled" ||
                delivery.State == "needsAttention" && !delivery.PublicationPending)
            {
                State = "notReady";
                Reason = $"Owned mission requires reconciliation ({delivery.State}); no automatic replay.";
                return;
            }
            var snapshotPath = Path.Combine(LocalState.RuntimeDirectory(root), "development-mission.json");
            var resume = File.Exists(snapshotPath) && LocalState.Read<DevelopmentMissionDelivery>(snapshotPath).Mission.Id == delivery.Mission.Id;
            LocalState.Write(snapshotPath, delivery);
            worker = await WslMissionWorker.StartAsync(plant, binding, configuration, root, delivery, resume, cancellationToken);
            State = "busy";
            Reason = "Owned frozen mission running through the Linux MAF/Copilot worker.";
        }
        catch (PlantConnectionException error) when (error.Code is "development_execution_disabled" or
            "development_policy_unconfigured" or "development_not_permitted" or "issue_not_eligible" or
            "repository_connection_unconfigured" or "repository_write_disabled" or "mission_owned" or
            "issue_owned" or "workload_changed" or "mission_revoked" or "issue_changed" or
            "copilot_reauthorization_required" or "copilot_refresh_pending" or "model_access_not_authorized")
        {
            if (error.Code == "workload_changed")
            {
                WorkbenchWorkloadStore.Revoke(root);
                File.Delete(Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-claim.json"));
            }
            State = "notReady";
            Reason = $"Development execution blocked: {error.Code}. Retain ownership and reconcile before retrying.";
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {Reason} Correlation: {error.CorrelationId ?? "unavailable"}.");
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(LocalState.RuntimeDirectory(root), "active-development-worker.json");
        if (!File.Exists(path) || worker is not null)
            return;
        var previous = LocalState.Read<WslWorkerJournal>(path);
        if (!await WslMissionWorker.StopRecordedAsync(configuration, previous, cancellationToken, root))
            throw new IOException("Prior Linux execution termination is unconfirmed; new work is blocked even when execution is disabled.");
        await plant.WriteAsync(HttpMethod.Post, $"v1/enrollments/{binding.EnrollmentId}/mission/terminated",
            new MissionTerminationRequest(previous.MissionId, true), Guid.NewGuid().ToString("D"), cancellationToken);
        File.Delete(path);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (worker is null)
            return;
        await worker.StopAsync(cancellationToken);
        try { await worker.Completion; }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Console.Error.WriteLine("Owned worker completion reconciled after confirmed Linux group termination.");
        }
        worker.Dispose();
        worker = null;
        State = "notReady";
        Reason = "Owned Linux process group termination confirmed; mission/result journals are retained.";
    }
}
