using Factory.Contracts;

namespace Factory.Workbench;

public sealed class WorkbenchDevelopmentExecution(PlantHttp plant, WorkbenchBinding binding,
    WorkbenchConfiguration localConfiguration, string root)
{
    private readonly WorkbenchConfiguration local = localConfiguration;
    private WorkbenchConfiguration configuration = localConfiguration;
    private WslMissionWorker? worker;
    private WorkbenchLineConfiguration? current;
    public string State { get; private set; } = "notReady";
    public string Reason { get; private set; } = "Development execution is disabled.";

    public async Task ObserveAsync(WorkbenchLineConfiguration observed, CancellationToken cancellationToken)
    {
        current = observed;
        configuration = WorkbenchExecutionConfiguration.Resolve(local, observed);
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
                : "Development execution is disabled. Configure the assigned line, prepare the SDK worker and select Start line in Setup.";
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
            State = "notReady";
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
            State = "notReady";
            Reason = "Resolved project verification commands are required before execution.";
            return;
        }
        if (workload.Validate().Workflow.CompletionCriterion != "pull_request_merged")
        {
            State = "notReady";
            Reason = "Declare workflow.completionCriterion as pull_request_merged in the repository workload before execution.";
            return;
        }
        var endpoint = $"v1/enrollments/{binding.EnrollmentId}/mission";
        var pendingPath = Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-queue.json");
        try
        {
            var legacyPath = Path.Combine(LocalState.RuntimeDirectory(root), "pending-development-claim.json");
            if (File.Exists(legacyPath))
            {
                var legacy = LocalState.Read<DevelopmentClaimRequest>(legacyPath);
                var migrated = new DevelopmentQueueRequest(legacy.OperationId, legacy.Configuration, legacy.WorkloadDigest, legacy.Model);
                if (File.Exists(pendingPath) && LocalState.Read<DevelopmentQueueRequest>(pendingPath) != migrated)
                    throw new InvalidDataException("Both legacy and queue claim intents exist; reconcile them before claiming work.");
                LocalState.Write(pendingPath, migrated);
                File.Delete(legacyPath);
            }
            var request = File.Exists(pendingPath) ? LocalState.Read<DevelopmentQueueRequest>(pendingPath)
                : new(Guid.NewGuid().ToString("D"), WorkloadRequest.From(current), workload.Digest, configuration.Development.Model);
            LocalState.Write(pendingPath, request);
            var selection = await plant.SendAsync<DevelopmentQueueSelection>(HttpMethod.Post, endpoint + "/next",
                request, request.OperationId, cancellationToken);
            File.Delete(pendingPath);
            var delivery = selection.Mission;
            if (delivery is null || delivery.State == "completed")
            {
                State = "idle";
                Reason = delivery is null
                    ? "No unclaimed eligible issues. The line will check the queue again."
                    : "Owned PR merged. Confirmed prior-worker termination is required before selecting the next issue.";
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
            "issue_owned" or "issue_queue_limit" or "configuration_changed" or "workload_changed" or "mission_revoked" or "issue_changed" or
            "copilot_reauthorization_required" or "copilot_refresh_pending" or "model_access_not_authorized")
        {
            if (error.Code is "workload_changed" or "configuration_changed" or "issue_owned")
            {
                if (error.Code == "workload_changed")
                    WorkbenchWorkloadStore.Revoke(root);
                File.Delete(pendingPath);
            }
            State = error.Code == "issue_owned" ? "idle" : "notReady";
            Reason = error.Code switch
            {
                "issue_owned" => "Another mission claimed the selected issue. The line will check the remaining queue again.",
                "copilot_reauthorization_required" => "Connect or reauthorize the line owner's account in Factory > Copilot access. No local GitHub login is needed.",
                "repository_connection_unconfigured" or "repository_write_disabled" =>
                    "Verify the selected connection and publication access in Factory > Repository connections.",
                "development_execution_disabled" => "Enable execution on the assigned production line. An explicit service shutdown setting also blocks execution.",
                "development_policy_unconfigured" => "Configure the model, commit author and trusted reviewers on the assigned production line.",
                _ => $"Development execution blocked: {error.Code}. Retain ownership and reconcile before retrying."
            };
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
