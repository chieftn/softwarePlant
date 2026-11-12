using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Workbench.Contracts;

namespace Workbench.Worker;

public interface IAgentRunner
{
    Task<string> RunAsync(WorkerRequest request, string workspace,
        Action<WorkerEvent> progress, CancellationToken cancellationToken);
}

public sealed class CopilotAgentRunner : IAgentRunner
{
    public async Task<string> RunAsync(WorkerRequest request, string workspace,
        Action<WorkerEvent> progress, CancellationToken cancellationToken)
    {
        await using var client = new CopilotClient(new CopilotClientOptions { WorkingDirectory = workspace });
        await client.StartAsync(cancellationToken);
        var configuration = new SessionConfig
        {
            Model = request.Model,
            WorkingDirectory = workspace,
            EnableFileHooks = false,
            OnPermissionRequest = (permission, _) =>
            {
                var allowed = PermissionPolicy.Allows(permission, workspace, request.AllowShellExecution);
                progress(new("progress", request.Assignment.Id, "permission",
                    $"{permission.Kind}: {(allowed ? "approved" : "denied")}"));
                // The pinned SDK marks its native permission decisions as experimental.
#pragma warning disable GHCP001
                return Task.FromResult(allowed
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject("Workbench policy does not permit this operation."));
#pragma warning restore GHCP001
            }
        };
        var agent = client.AsAIAgent(configuration, name: request.Methods.WorkerId);
        var response = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(
            BuildPrompt(request), cancellationToken: cancellationToken))
        {
            var text = update.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                response.Append(text);
                progress(new("progress", request.Assignment.Id, "agent", text));
            }
        }
        return response.ToString();
    }

    private static string BuildPrompt(WorkerRequest request) => $"""
        You are a coding worker at an assigned workbench.
        Production line: {request.Methods.LineId}, revision {request.Methods.LineVersion}
        Product business problem: {request.Methods.BusinessProblem}
        Worker: {request.Methods.WorkerId}
        Role: {request.Methods.Role}

        Production guidance:
        {string.Join("\n", request.Methods.Guidance)}

        Applicable skills:
        {string.Join("\n\n", request.Methods.SkillInstructions)}

        Assignment:
        {request.Assignment.Instructions}

        Acceptance criteria:
        {string.Join("\n", request.Assignment.AcceptanceCriteria.Select(item => "- " + item))}

        Execution boundary:
        Modify and verify the assigned checkout only. Do not change branches, commit, push,
        create a PR, or modify Git configuration or metadata. The host owns those operations.
        Do not weaken acceptance criteria or tests to manufacture a passing result.
        End with a factual summary of changes, checks, and unresolved concerns.
        """;
}

public static class PermissionPolicy
{
    public static bool Allows(PermissionRequest request, string workspace, bool allowShell)
    {
        if (request.ManagedApprovalRequired == true)
            return false;
        return request switch
        {
            PermissionRequestRead read => read.RequestSandboxBypass != true && Within(workspace, read.Path),
            PermissionRequestWrite write => write.RequestSandboxBypass != true && Within(workspace, write.FileName),
            PermissionRequestShell shell => allowShell && shell.RequestSandboxBypass != true,
            _ => false
        };
    }

    private static bool Within(string workspace, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var root = Path.GetFullPath(workspace);
        var resolved = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, resolved);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            && !Path.IsPathRooted(relative)
            && relative != ".git" && !relative.StartsWith(".git" + Path.DirectorySeparatorChar);
    }
}
