namespace Factory.Contracts;

public sealed record ModelAccessCredential(string Provider, string Model, string AccessToken, DateTimeOffset ExpiresAt)
{
    public void Validate(string model, DateTimeOffset now)
    {
        ContractValidation.Require(Provider == "github-copilot" && Model == model &&
            !string.IsNullOrWhiteSpace(AccessToken) && !AccessToken.Any(char.IsWhiteSpace) &&
            ExpiresAt > now.AddMinutes(65),
            "The authorized model credential must match the session and remain valid for more than 65 minutes.");
    }

    public override string ToString() => $"ModelAccessCredential ({Provider}; {Model}; token redacted)";
}

public sealed record MissionModelCredentialRequest(string MissionId, string MissionDigest);
