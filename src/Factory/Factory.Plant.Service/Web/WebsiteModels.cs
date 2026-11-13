using Factory.Contracts;

namespace Factory.Plant.Service.Web;

public sealed class CreatePlantForm
{
    public string Name { get; set; } = "";
    public string OperationKey { get; set; } = "";
}

public sealed record PlantsPage(Page<PlantView> Plants);
public sealed record WorkbenchesPage(string PlantId, Page<WorkbenchView> Workbenches);
