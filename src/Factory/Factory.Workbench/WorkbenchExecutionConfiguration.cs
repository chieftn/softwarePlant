using Factory.Contracts;

namespace Factory.Workbench;

public static class WorkbenchExecutionConfiguration
{
    public static WorkbenchConfiguration Resolve(WorkbenchConfiguration local, WorkbenchLineConfiguration desired)
    {
        var execution = desired.ProductionLine?.Definition.Execution;
        if (execution is null && local.ExecutionConsent is null)
            return local;
        execution?.Validate();
        return local with
        {
            Development = (local.Development ?? new()) with
            {
                Enabled = execution?.Enabled == true && local.ExecutionConsent?.Matches(desired) == true,
                Model = execution?.Model ?? "",
                GitAuthorName = execution?.GitAuthorName ?? "",
                GitAuthorEmail = execution?.GitAuthorEmail ?? ""
            }
        };
    }
}
