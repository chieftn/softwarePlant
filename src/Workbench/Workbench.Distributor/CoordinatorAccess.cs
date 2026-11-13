using System.Security.Cryptography;
using System.Text;
using Workbench.Contracts;

namespace Workbench.Distributor;

public sealed class CoordinatorAccess
{
    private readonly bool local;
    private readonly string[] operators;
    private readonly Dictionary<string, string> workbenches;
    private readonly string? localOperatorToken;

    public CoordinatorAccess(IConfiguration configuration, bool local)
    {
        this.local = local;
        if (local)
        {
            localOperatorToken = configuration["Coordinator:Local:OperatorToken"]
                ?? throw new InvalidOperationException("Local operator token is required.");
            workbenches = Read(configuration, "Coordinator:Local:Workbenches");
            operators = [];
            var tokens = workbenches.Values.Append(localOperatorToken).ToArray();
            if (tokens.Any(value => value.Length < 32) || tokens.Distinct().Count() != tokens.Length)
                throw new InvalidOperationException("Local tokens must be distinct and at least 32 characters.");
        }
        else
        {
            operators = configuration.GetSection("Coordinator:Operators").GetChildren()
                .Select(item => item.Value ?? "").ToArray();
            workbenches = Read(configuration, "Coordinator:Workbenches");
            var identities = operators.Concat(workbenches.Values).ToArray();
            if ((operators.Length == 0 && workbenches.Count != 0) || identities.Any(string.IsNullOrWhiteSpace) ||
                identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
                throw new InvalidOperationException("Configure distinct Foundry caller IDs for operators and workbenches.");
        }
        foreach (var id in workbenches.Keys)
            DispatchRequest.ValidateWorkbenchId(id);
    }

    public CoordinatorCaller Authenticate(HttpRequest request)
    {
        if (!local)
        {
            var user = request.Headers["x-agent-user-id"].ToString();
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(request.Headers["x-agent-foundry-call-id"]))
                throw new UnauthorizedAccessException("Foundry protocol 2.0 caller context is required.");
            return new(user, operators.Contains(user, StringComparer.Ordinal),
                workbenches.FirstOrDefault(item => item.Value == user).Key);
        }
        if (request.Headers.ContainsKey("x-agent-user-id") || request.Headers.ContainsKey("x-agent-foundry-call-id"))
            throw new UnauthorizedAccessException("Local mode does not trust platform identity headers.");
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Local bearer authentication is required.");
        var token = authorization["Bearer ".Length..];
        if (Matches(token, localOperatorToken!))
            return new("local:operator", true, null);
        var workbench = workbenches.FirstOrDefault(item => Matches(token, item.Value)).Key;
        return workbench is not null ? new("local:" + workbench, false, workbench)
            : throw new UnauthorizedAccessException("Unknown local credential.");
    }

    public bool KnowsWorkbench(string id) => workbenches.ContainsKey(id);

    private static Dictionary<string, string> Read(IConfiguration configuration, string section) =>
        configuration.GetSection(section).GetChildren().ToDictionary(item => item.Key,
            item => item.Value ?? throw new InvalidOperationException("Each identity needs a value."), StringComparer.Ordinal);

    private static bool Matches(string token, string expected) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

public sealed record CoordinatorCaller(string PrincipalId, bool IsOperator, string? WorkbenchId);
