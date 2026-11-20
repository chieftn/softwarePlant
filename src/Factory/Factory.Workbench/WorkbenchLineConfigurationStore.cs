using Factory.Contracts;

namespace Factory.Workbench;

public sealed record WorkbenchLineState(string SchemaVersion, string InstallationId,
    WorkbenchLineConfiguration Configuration, WorkbenchConfigurationObservation Observation);

public static class WorkbenchLineConfigurationStore
{
    public static string PathFor(string root) => Path.Combine(LocalState.RuntimeDirectory(root), "line-configuration.json");

    public static WorkbenchLineState? Read(string root, WorkbenchBinding binding)
    {
        var path = PathFor(root);
        if (!File.Exists(path))
            return null;
        var state = LocalState.Read<WorkbenchLineState>(path);
        if (state.SchemaVersion != "1" || state.InstallationId != binding.InstallationId)
            throw new InvalidDataException("Line configuration belongs to another installation.");
        Validate(state.Configuration, binding);
        state.Observation.Validate();
        if (state.Observation.AssignmentVersion != state.Configuration.AssignmentVersion ||
            state.Observation.BindingVersion != state.Configuration.BindingVersion ||
            state.Observation.ProductionLineId != state.Configuration.ProductionLine?.Id ||
            state.Observation.ProductionLineRevision != state.Configuration.ProductionLine?.Revision)
            throw new InvalidDataException("Line configuration and observation identities do not match.");
        return state;
    }

    public static void ObserveMembership(string root, WorkbenchBinding binding, WorkbenchAssignment assignment)
    {
        var previous = Read(root, binding);
        if (previous is null)
            return;
        if (previous.Configuration.AssignmentVersion > assignment.Version)
            throw new InvalidDataException("Factory returned an obsolete membership version.");
        if (previous.Configuration.AssignmentVersion == assignment.Version &&
            previous.Configuration.PlantId != assignment.PlantId)
            throw new InvalidDataException("Factory returned conflicting membership identities.");
        if (previous.Configuration.AssignmentVersion != assignment.Version)
            Apply(root, binding, new("1", binding.EnrollmentId, binding.WorkbenchId, assignment.PlantId,
                assignment.Version, 0, null));
    }

    public static WorkbenchLineState Apply(string root, WorkbenchBinding binding, WorkbenchLineConfiguration desired)
    {
        Validate(desired, binding);
        var previous = Read(root, binding);
        if (previous is not null &&
            (previous.Configuration.AssignmentVersion > desired.AssignmentVersion ||
            previous.Configuration.AssignmentVersion == desired.AssignmentVersion &&
                previous.Configuration.BindingVersion > desired.BindingVersion))
            throw new InvalidDataException("Factory returned an obsolete line configuration.");
        if (previous is not null && previous.Configuration.AssignmentVersion == desired.AssignmentVersion &&
            previous.Configuration.PlantId != desired.PlantId)
            throw new InvalidDataException("Factory returned conflicting membership identities.");
        if (previous is not null && previous.Configuration.AssignmentVersion == desired.AssignmentVersion &&
            previous.Configuration.BindingVersion == desired.BindingVersion &&
            previous.Configuration.ProductionLine?.Id != desired.ProductionLine?.Id)
            throw new InvalidDataException("Factory returned conflicting line binding identities.");
        var revoked = previous?.Configuration.ProductionLine is not null &&
            (previous.Configuration.AssignmentVersion != desired.AssignmentVersion ||
                previous.Configuration.BindingVersion != desired.BindingVersion ||
                previous.Configuration.ProductionLine.Id != desired.ProductionLine?.Id);
        if (revoked)
        {
            WorkbenchWorkloadStore.Revoke(root);
            LocalState.Write(PathFor(root), previous! with
            {
                Observation = previous!.Observation with
                {
                    State = "stopping",
                    Reason = "Configuration revoked. Owned execution must stop; uncertain termination retains its mission claim."
                }
            });
        }
        var observation = new WorkbenchConfigurationObservation
        {
            SchemaVersion = "1",
            AssignmentVersion = desired.AssignmentVersion,
            BindingVersion = desired.BindingVersion,
            ProductionLineId = desired.ProductionLine?.Id,
            ProductionLineRevision = desired.ProductionLine?.Revision,
            State = desired.ProductionLine is null ? "unconfigured" : "blocked",
            Reason = desired.ProductionLine is null
                ? "No production line is assigned. No worker execution was started."
                : "Line configuration received. Repository specs must be resolved; worker execution remains disabled."
        };
        var current = new WorkbenchLineState("1", binding.InstallationId, desired, observation);
        LocalState.Write(PathFor(root), current);
        return current;
    }

    private static void Validate(WorkbenchLineConfiguration configuration, WorkbenchBinding binding)
    {
        if (configuration.SchemaVersion != "1" || configuration.EnrollmentId != binding.EnrollmentId ||
            configuration.WorkbenchId != binding.WorkbenchId || configuration.AssignmentVersion < 0 ||
            configuration.BindingVersion < 0 ||
            configuration.PlantId is { } plant && (!Guid.TryParseExact(plant, "D", out var id) ||
                id == Guid.Empty || configuration.AssignmentVersion == 0) ||
            configuration.PlantId is null && (configuration.BindingVersion != 0 || configuration.ProductionLine is not null))
            throw new InvalidDataException("Factory returned an invalid line configuration identity.");
        if (configuration.ProductionLine is { } line)
        {
            if (line.SchemaVersion != "1" || configuration.BindingVersion == 0 ||
                !Guid.TryParseExact(line.Id, "D", out var lineId) || lineId == Guid.Empty ||
                line.Revision.Length != 34 || line.Revision[0] != '"' || line.Revision[^1] != '"' ||
                !Guid.TryParseExact(line.Revision[1..^1], "N", out _))
                throw new InvalidDataException("Factory returned an invalid production-line revision.");
            try
            {
                line.Definition.Validate();
            }
            catch (ContractValidationException error)
            {
                throw new InvalidDataException("Factory returned an unsupported line definition.", error);
            }
        }
    }
}
