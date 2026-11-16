using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Factory.Contracts;

public sealed record WorkloadAgent(string Instructions, string WorkspaceAccess);
public sealed record WorkloadAgents(WorkloadAgent Implementation, WorkloadAgent Alignment);
public sealed record WorkloadWorkflow(string Type, int MaximumIterations, int TimeoutMinutes);
public sealed record WorkloadCommand(string Executable, string[] Arguments, string WorkingDirectory, int TimeoutSeconds);
public sealed record WorkloadCommands(WorkloadCommand[] Commands);

public sealed record RepositoryWorkload(
    [property: JsonPropertyName("$schema")] string Schema,
    string SchemaVersion, string Id, string Version, WorkloadAgents Agents, WorkloadWorkflow Workflow,
    WorkloadCommands Preparation, WorkloadCommands Verification, string[] RequiredOutputs)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    public static RepositoryWorkload Parse(string content)
    {
        var workload = ParseJson<RepositoryWorkload>(content);
        workload.Validate();
        return workload;
    }

    public static T ParseJson<T>(string content)
    {
        using var document = JsonDocument.Parse(content, new() { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(content, Json)
            ?? throw new ContractValidationException("Workload must contain an object.");
    }

    public void Validate()
    {
        ContractValidation.Require(Schema == "workload.schema.json" && SchemaVersion == "1",
            "Only repository workload schema version 1 is supported.");
        ContractValidation.Text(Id, 128, "workload.id");
        ContractValidation.Require(Regex.IsMatch(Id, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$"),
            "Workload id must use lowercase kebab-case.");
        ContractValidation.Text(Version, 128, "workload.version");
        ContractValidation.Require(Regex.IsMatch(Version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"),
            "Workload version must use major.minor.patch without leading zeros.");
        if (Agents?.Implementation is null || Agents.Alignment is null)
            throw new ContractValidationException("Both workload agent roles are required.");
        ContractValidation.Require(Agents.Implementation.WorkspaceAccess == "read-write" && Agents.Alignment.WorkspaceAccess == "read-only",
            "Implementation must request read-write and Alignment read-only access.");
        RelativePath(Agents.Implementation.Instructions);
        RelativePath(Agents.Alignment.Instructions);
        ContractValidation.Require(Agents.Implementation.Instructions != Agents.Alignment.Instructions &&
            new[] { Agents.Implementation.Instructions, Agents.Alignment.Instructions }
                .All(path => path is not ("workload.json" or "workload.schema.json")),
            "Distinct agent instructions must not replace the manifest or schema.");
        ContractValidation.Require(Workflow is not null && Workflow.Type == "implementation-alignment" &&
            Workflow.MaximumIterations is >= 1 and <= 20 && Workflow.TimeoutMinutes is >= 1 and <= 240,
            "Unsupported or unbounded workload workflow.");
        ValidateCommands(Preparation);
        ValidateCommands(Verification);
        ContractValidation.Require(RequiredOutputs is not null && RequiredOutputs.SequenceEqual(
            ["candidate", "verification-evidence", "alignment-report"]),
            "Required outputs must be candidate, verification-evidence and alignment-report.");
    }

    public static void RelativePath(string path)
    {
        ContractValidation.Text(path, 256, "workload path");
        ContractValidation.Require(Regex.IsMatch(path,
            @"^(?:[A-Za-z0-9_-][A-Za-z0-9._-]*/)*[A-Za-z0-9_-][A-Za-z0-9._-]*$"),
            "Workload paths must be relative without traversal or hidden components.");
    }

    public static void Commit(string value) => ContractValidation.Require(value is { Length: 40 } &&
        value.All(char.IsAsciiHexDigit), "An exact Git SHA-1 commit is required.");

    private static void ValidateCommands(WorkloadCommands commands)
    {
        ContractValidation.Require(commands?.Commands is { Length: <= 32 }, "At most 32 commands are supported.");
        foreach (var command in commands!.Commands)
        {
            ContractValidation.Require(command is not null, "Commands cannot be null.");
            ContractValidation.Text(command!.Executable, 128, "executable");
            ContractValidation.Require(Regex.IsMatch(command.Executable, "^[A-Za-z0-9][A-Za-z0-9._-]*$"),
                "Command executable must be a bare executable name.");
            ContractValidation.Require(command.Arguments is { Length: <= 64 } &&
                command.Arguments.All(argument => argument is { Length: <= 4096 } &&
                    !argument.Any(character => character is '\0' or '\r' or '\n')),
                "Command arguments must be bounded and contain no NUL or newline.");
            if (command.WorkingDirectory != ".")
                RelativePath(command.WorkingDirectory);
            ContractValidation.Require(command.TimeoutSeconds is >= 1 and <= 14400,
                "Command timeout must be between 1 and 14400 seconds.");
        }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ContractValidationException("Duplicate JSON properties are unsupported.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                RejectDuplicates(item);
    }
}

public sealed record WorkloadRequest(long AssignmentVersion, long BindingVersion,
    string ProductionLineId, string ProductionLineRevision)
{
    public bool Matches(WorkbenchLineConfiguration configuration) =>
        AssignmentVersion == configuration.AssignmentVersion && BindingVersion == configuration.BindingVersion &&
        ProductionLineId == configuration.ProductionLine?.Id &&
        ProductionLineRevision == configuration.ProductionLine?.Revision;

    public static WorkloadRequest From(WorkbenchLineConfiguration configuration) => new(
        configuration.AssignmentVersion, configuration.BindingVersion,
        configuration.ProductionLine?.Id ?? throw new ContractValidationException("No line is assigned."),
        configuration.ProductionLine.Revision);
}

public sealed record ResolvedRepositoryWorkload(string SchemaVersion, WorkbenchLineConfiguration Configuration,
    string SourceCommit, string Digest, Dictionary<string, string> Files)
{
    public static ResolvedRepositoryWorkload Create(WorkbenchLineConfiguration configuration, string commit,
        Dictionary<string, string> files) => new("1", configuration, commit, ComputeDigest(configuration, commit, files), files);

    public RepositoryWorkload Validate()
    {
        ContractValidation.Require(SchemaVersion == "1" && Configuration?.ProductionLine is not null,
            "Resolved workload must identify its configured production line.");
        Configuration!.ProductionLine!.Definition.Validate();
        RepositoryWorkload.Commit(SourceCommit);
        ContractValidation.Require(Files is { Count: 4 } && Files.Values.All(content =>
            !string.IsNullOrWhiteSpace(content) && Encoding.UTF8.GetByteCount(content) <= 16 * 1024),
            "Workload must contain four nonempty bounded UTF-8 files.");
        ContractValidation.Require(Files!.ContainsKey("workload.json") && Files.ContainsKey("workload.schema.json"),
            "Workload manifest and authoring schema are required.");
        var workload = RepositoryWorkload.Parse(Files["workload.json"]);
        ContractValidation.Require(Files.ContainsKey(workload.Agents.Implementation.Instructions) &&
            Files.ContainsKey(workload.Agents.Alignment.Instructions), "Agent instruction files are required.");
        foreach (var path in Files.Keys)
            RepositoryWorkload.RelativePath(path);
        using var schema = JsonDocument.Parse(Files["workload.schema.json"]);
        ContractValidation.Require(schema.RootElement.ValueKind == JsonValueKind.Object,
            "Authoring schema must contain a JSON object.");
        ContractValidation.Require(Digest == ComputeDigest(Configuration, SourceCommit, Files),
            "Resolved workload digest does not match its immutable contents.");
        ContractValidation.Require(JsonSerializer.SerializeToUtf8Bytes(this, RepositoryWorkload.Json).Length <= 96 * 1024,
            "Resolved workload exceeds the local state/transport bound.");
        return workload;
    }

    private static string ComputeDigest(WorkbenchLineConfiguration configuration, string commit,
        Dictionary<string, string> files) => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                configuration,
                commit,
                files = files.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray()
            }, RepositoryWorkload.Json)));
}
