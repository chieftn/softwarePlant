using Factory.Contracts;

namespace Factory.Plant.Service.Web;

public sealed class CreatePlantForm
{
    public string Name { get; set; } = "";
    public string OperationKey { get; set; } = "";
}

public sealed record PlantsPage(Page<PlantView> Plants);
public sealed record WorkbenchesPage(string PlantId, Page<WorkbenchView> Workbenches);
public sealed record EnrollmentsPage(Page<EnrollmentView> Enrollments, IReadOnlyList<PlantView> Plants);

public sealed record ProductionLinesPage(Page<ProductionLineView> Lines);

public sealed class ProductionLineForm
{
    public string? LineId { get; set; }
    public string Revision { get; set; } = "";
    public string OperationKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceRepository { get; set; } = "";
    public string BaseBranch { get; set; } = "main";
    public string WorkloadPath { get; set; } = "productionLine";
    public string SourceConnection { get; set; } = "";
    public string TaskRepository { get; set; } = "";
    public string TaskConnection { get; set; } = "";
    public string RequiredLabels { get; set; } = "";
    public bool AllowShellExecution { get; set; }
    public bool AllowPublication { get; set; }
    public int MaximumIterations { get; set; } = 3;
    public int TimeoutMinutes { get; set; } = 30;

    public ProductionLineWriteRequest Definition() => new()
    {
        SchemaVersion = "1",
        Name = Name,
        Source = new("github", SourceRepository, BaseBranch, WorkloadPath, SourceConnection),
        Tasks = new("github-issues", TaskRepository, TaskConnection,
            (RequiredLabels ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
        Policy = new(AllowShellExecution, AllowPublication, MaximumIterations, TimeoutMinutes)
    };

    public static ProductionLineForm From(ProductionLineView line) => new()
    {
        LineId = line.Id,
        Revision = line.Revision,
        OperationKey = Guid.NewGuid().ToString("D"),
        Name = line.Definition.Name,
        SourceRepository = line.Definition.Source.Repository,
        BaseBranch = line.Definition.Source.BaseBranch,
        WorkloadPath = line.Definition.Source.WorkloadPath,
        SourceConnection = line.Definition.Source.ConnectionReference,
        TaskRepository = line.Definition.Tasks.Repository,
        TaskConnection = line.Definition.Tasks.ConnectionReference,
        RequiredLabels = string.Join(", ", line.Definition.Tasks.RequiredLabels),
        AllowShellExecution = line.Definition.Policy.AllowShellExecution,
        AllowPublication = line.Definition.Policy.AllowPublication,
        MaximumIterations = line.Definition.Policy.MaximumIterations,
        TimeoutMinutes = line.Definition.Policy.TimeoutMinutes
    };
}

public sealed class ProductionLineAssignmentForm
{
    public string PlantId { get; set; } = "";
    public string WorkbenchId { get; set; } = "";
    public long ExpectedVersion { get; set; }
    public string? ProductionLineId { get; set; }
    public WorkbenchView? Workbench { get; set; }
    public WorkbenchLineConfiguration? DesiredConfiguration { get; set; }
}
