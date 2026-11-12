using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static Workbench.Contracts.Assignment;

namespace Workbench.Contracts;

public sealed record CommandSpec
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
}

public sealed record Assignment
{
    public string SchemaVersion { get; init; } = "0.1";
    public required string Id { get; init; }
    public required string Repository { get; init; }
    public string BaseBranch { get; init; } = "main";
    public required string Instructions { get; init; }
    public required string PullRequestTitle { get; init; }
    public required string[] AcceptanceCriteria { get; init; }
    public required CommandSpec[] VerificationCommands { get; init; }

    [JsonIgnore]
    public string Branch => $"workbench/{Id}";

    public void Validate()
    {
        Require(SchemaVersion == "0.1", "Unsupported assignment schemaVersion.");
        Require(Id is not null && Regex.IsMatch(Id, @"\A[a-z0-9][a-z0-9-]{0,62}\z"), "Invalid assignment ID.");
        Require(Repository is not null &&
            Regex.IsMatch(Repository, @"\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z"),
            "Repository must be a GitHub owner/repository name, without credentials or a URL.");
        Require(!string.IsNullOrWhiteSpace(BaseBranch) && !BaseBranch.StartsWith('-'),
            "A base branch is required.");
        Require(!string.IsNullOrWhiteSpace(Instructions), "Instructions are required.");
        Require(!string.IsNullOrWhiteSpace(PullRequestTitle), "A PR title is required.");
        Require(AcceptanceCriteria is { Length: > 0 } &&
            AcceptanceCriteria.All(item => !string.IsNullOrWhiteSpace(item)), "Acceptance criteria are required.");
        Require(VerificationCommands is { Length: > 0 }, "At least one verification command is required.");
        foreach (var command in VerificationCommands!)
        {
            Require(command is not null && !string.IsNullOrWhiteSpace(command.Executable),
                "Each verification command needs an executable.");
            Require(command!.Arguments is not null && command.Arguments.All(item => item is not null),
                "Command arguments must be strings.");
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

public sealed record ProductionMethods
{
    public required string LineId { get; init; }
    public required string LineVersion { get; init; }
    public required string BusinessProblem { get; init; }
    public required string WorkerId { get; init; }
    public required string Role { get; init; }
    public required string[] Guidance { get; init; }
    public required string[] SkillInstructions { get; init; }
    public required string[] ToolCapabilities { get; init; }

    public void Validate()
    {
        Assignment.Require(
            !string.IsNullOrWhiteSpace(LineId) && !string.IsNullOrWhiteSpace(LineVersion) &&
            !string.IsNullOrWhiteSpace(WorkerId) && !string.IsNullOrWhiteSpace(Role) &&
            !string.IsNullOrWhiteSpace(BusinessProblem),
            "Line revision, worker identity and role are required.");
        Assignment.Require(Guidance is not null && SkillInstructions is not null && ToolCapabilities is not null,
            "Production method collections are required.");
        Assignment.Require(Guidance!.Concat(SkillInstructions!).Concat(ToolCapabilities!)
            .All(item => !string.IsNullOrWhiteSpace(item)), "Production methods cannot contain blank entries.");
    }

    public static ProductionMethods Load(string path, string workerId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assignment.Require(root.GetProperty("schemaVersion").GetString() == "0.1",
            "Unsupported production-line schemaVersion.");
        var methods = root.GetProperty("productionMethods");
        var workers = Index(methods.GetProperty("workers"));
        var skills = Index(methods.GetProperty("skills"));
        var tools = Index(methods.GetProperty("tools"));
        Assignment.Require(workers.TryGetValue(workerId, out var worker), $"Unknown worker: {workerId}");
        Assignment.Require(worker.GetProperty("occupation").GetString() == "coder",
            "This execution slice supports the coder occupation.");
        var skillRefs = Strings(worker.GetProperty("skillRefs"));
        var toolRefs = Strings(worker.GetProperty("toolRefs"));
        Assignment.Require(skillRefs.All(skills.ContainsKey) && toolRefs.All(tools.ContainsKey),
            "Worker skill/tool references must resolve within the line.");
        var result = new ProductionMethods
        {
            LineId = root.GetProperty("id").GetString()!,
            LineVersion = root.GetProperty("version").GetString()!,
            BusinessProblem = root.GetProperty("product").GetProperty("businessProblem").GetString()!,
            WorkerId = workerId,
            Role = worker.GetProperty("role").GetString()!,
            Guidance = Strings(methods.GetProperty("guidance")),
            SkillInstructions = skillRefs.Select(id => skills[id].GetProperty("instructions").GetString()!).ToArray(),
            ToolCapabilities = toolRefs
        };
        result.Validate();
        return result;
    }

    private static Dictionary<string, JsonElement> Index(JsonElement array)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            Assignment.Require(!string.IsNullOrWhiteSpace(id) && index.TryAdd(id, item),
                "Worker, skill and tool IDs must be nonempty and unique within their collection.");
        }
        return index;
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()
            ?? throw new InvalidDataException("Expected a string.")).ToArray();
}

public sealed record WorkerRequest
{
    public required Assignment Assignment { get; init; }
    public required ProductionMethods Methods { get; init; }
    public bool AllowShellExecution { get; init; }
    public bool PublishPullRequest { get; init; }
    public int TimeoutSeconds { get; init; } = 1800;
    public string? Model { get; init; }

    public void Validate()
    {
        Require(Assignment is not null && Methods is not null, "Assignment and methods are required.");
        Assignment!.Validate();
        Methods!.Validate();
        Require(TimeoutSeconds is >= 1 and <= 86400, "Timeout must be between 1 and 86400 seconds.");
        Require(
            Methods.ToolCapabilities.Contains("workspace") &&
            Methods.ToolCapabilities.Contains("verification") &&
            Methods.ToolCapabilities.Contains("source-control"),
            "The coder requires workspace, verification and source-control capabilities.");
        Require(Methods.ToolCapabilities
            .All(tool => tool is "workspace" or "verification" or "source-control"),
            "This worker adapter supports only workspace, verification and source-control capabilities.");
    }
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated = false);
public sealed record VerificationResult(CommandSpec Command, CommandResult Result);

public sealed record WorkerResult
{
    public required string AssignmentId { get; init; }
    public required string Status { get; init; }
    public string? Workspace { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? PullRequestUrl { get; init; }
    public string? AgentResponse { get; init; }
    public string? Error { get; init; }
    public VerificationResult[] Verification { get; init; } = [];
}

public sealed record WorkerEvent(string Type, string AssignmentId, string Stage, string? Message = null, WorkerResult? Result = null);

public static class Wire
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new InvalidDataException("Expected a JSON object.");

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
