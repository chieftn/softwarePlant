using System.Security.Cryptography;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;

namespace Factory.Plant.Service;

public sealed partial class EnrollmentOperations
{
    public async Task<WorkbenchLineConfiguration> ConfigurationAsync(string id, ApiCaller caller,
        CancellationToken cancellationToken)
    {
        var enrollment = Records.Require<EnrollmentRecord>((await RequireMachineAsync(id, caller, cancellationToken)).Record);
        if (enrollment.State != "active")
            throw ApiProblems.Forbidden();
        await EnsureAssignmentAsync(enrollment, cancellationToken);
        return await plants.MachineConfigurationAsync(enrollment, cancellationToken);
    }

    public async Task ObserveConfigurationAsync(string id, ApiCaller caller, string key,
        WorkbenchConfigurationObservation request, CancellationToken cancellationToken)
    {
        request.Validate();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Records.Json)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await RequireMachineAsync(id, caller, cancellationToken);
            var enrollment = Records.Require<EnrollmentRecord>(stored.Record);
            if (enrollment.State != "active")
                throw ApiProblems.Forbidden();
            var receiptId = $"configuration-{key}";
            var receipt = await store.ReadAsync(enrollment.ScopeId, receiptId, cancellationToken);
            if (receipt is not null)
            {
                if (Records.Require<OperationRecord>(receipt.Record).Digest != digest)
                    throw ApiProblems.Conflict();
                return;
            }
            var current = await plants.MachineConfigurationAsync(enrollment, cancellationToken);
            if (request.AssignmentVersion != current.AssignmentVersion || request.BindingVersion != current.BindingVersion ||
                request.ProductionLineId != current.ProductionLine?.Id ||
                request.ProductionLineRevision != current.ProductionLine?.Revision)
                throw new ApiException(409, "configuration_changed", "Configuration was superseded or revoked. Discover the current configuration.");
            var previous = await store.ReadAsync(enrollment.ScopeId, "configuration-observation", cancellationToken);
            var now = clock.GetUtcNow();
            if (await store.CommitAsync(enrollment.ScopeId,
            [
                new(enrollment, stored.ETag),
                new(new ConfigurationObservationRecord
                {
                    Id = "configuration-observation", ScopeId = enrollment.ScopeId, EnrollmentId = id, PlantId = current.PlantId,
                    Observation = request, RecordedAt = now
                }, previous?.ETag),
                new(new OperationRecord { Id = receiptId, ScopeId = enrollment.ScopeId, Digest = digest, RecordedAt = now })
            ], cancellationToken))
                return;
        }
        throw ApiProblems.Unavailable();
    }
}
