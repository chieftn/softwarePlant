namespace Factory.Contracts;

public sealed record WorkbenchConfiguration
{
    public required string SchemaVersion { get; init; }
    public required string InstallationId { get; init; }
    public required string PlantServiceUrl { get; init; }
    public required string TenantId { get; init; }
    public required string ApiClientId { get; init; }
    public required string InstallerClientId { get; init; }
    public required string Distribution { get; init; }
    public required string LinuxUser { get; init; }
    public required string LinuxWorkspaceRoot { get; init; }
    public required string[] AllowedRepositories { get; init; }
    public required bool AllowShellExecution { get; init; }
    public required bool AllowPublication { get; init; }
    public WorkbenchDevelopmentConfiguration? Development { get; init; }

    public void Validate()
    {
        ContractValidation.Version(SchemaVersion);
        Identifier(InstallationId, nameof(InstallationId));
        Identifier(TenantId, nameof(TenantId));
        Identifier(ApiClientId, nameof(ApiClientId));
        Identifier(InstallerClientId, nameof(InstallerClientId));
        ContractValidation.Require(Uri.TryCreate(PlantServiceUrl, UriKind.Absolute, out var service) &&
            service.Scheme == Uri.UriSchemeHttps && service.UserInfo.Length == 0 &&
            service.AbsolutePath == "/" && service.Query.Length == 0 && service.Fragment.Length == 0,
            "PlantServiceUrl must be an HTTPS origin without credentials, path, query, or fragment.");
        ContractValidation.Text(Distribution, 128, nameof(Distribution));
        ContractValidation.Require(char.IsAsciiLetterOrDigit(Distribution[0]) && Distribution.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'),
            "Distribution must contain only ASCII letters, digits, dots, underscores, or hyphens.");
        ContractValidation.Text(LinuxUser, 32, nameof(LinuxUser));
        ContractValidation.Require((char.IsAsciiLetterLower(LinuxUser[0]) || LinuxUser[0] == '_') &&
            LinuxUser.All(character => char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) || character is '_' or '-'),
            "LinuxUser must be a Linux account name, not a command or option.");
        ContractValidation.Text(LinuxWorkspaceRoot, 1024, nameof(LinuxWorkspaceRoot));
        ContractValidation.Require(LinuxWorkspaceRoot.StartsWith('/') && LinuxWorkspaceRoot != "/" &&
            !LinuxWorkspaceRoot.Contains('\\') && !LinuxWorkspaceRoot.Any(char.IsControl) &&
            !LinuxWorkspaceRoot.Split('/').Any(part => part is "." or ".."),
            "LinuxWorkspaceRoot must be an absolute, non-root Linux path without dot segments.");
        ContractValidation.Require(AllowedRepositories is { Length: <= 128 },
            "AllowedRepositories must be an array with at most 128 entries.");
        foreach (var repository in AllowedRepositories!)
        {
            ContractValidation.Text(repository, 256, "repository");
            var parts = repository.Split('/');
            ContractValidation.Require(parts.Length == 2 && parts.All(part =>
                part.Length > 0 && part is not ("." or "..") &&
                part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')),
                "Each repository must use the GitHub owner/repository form, not a URL or wildcard.");
        }
        ContractValidation.Require(AllowedRepositories.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            AllowedRepositories.Length, "AllowedRepositories must contain distinct entries.");
        if (Development?.Enabled == true)
        {
            Development.Validate();
            ContractValidation.Require(AllowShellExecution && AllowPublication && AllowedRepositories.Length > 0,
                "Development execution requires explicit local shell/publication permissions and repository allowlist.");
        }
    }

    private static void Identifier(string? value, string name) =>
        ContractValidation.Require(Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty,
            $"{name} must be a nonempty UUID.");
}

public sealed record WorkbenchDevelopmentConfiguration
{
    public bool Enabled { get; init; }
    public long AssignedIssueNumber { get; init; }
    public string WorkerExecutable { get; init; } = "";
    public string Model { get; init; } = "";
    public string GitAuthorName { get; init; } = "";
    public string GitAuthorEmail { get; init; } = "";

    public void Validate()
    {
        ContractValidation.Require(AssignedIssueNumber > 0, "Specify the explicitly assigned GitHub issue number.");
        ContractValidation.Text(WorkerExecutable, 1024, "workerExecutable");
        ContractValidation.Require(WorkerExecutable.StartsWith('/') && WorkerExecutable != "/" &&
            !WorkerExecutable.Contains('\\') && !WorkerExecutable.Any(char.IsControl) &&
            !WorkerExecutable.Split('/').Any(part => part is "." or ".."),
            "Worker executable must be an installed absolute Linux path without traversal.");
        ContractValidation.Text(Model, 128, "model");
        ContractValidation.Text(GitAuthorName, 128, "Git author name");
        ContractValidation.Text(GitAuthorEmail, 254, "Git author email");
    }
}
