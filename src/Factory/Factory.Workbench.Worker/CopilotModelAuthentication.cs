using Factory.Contracts;
using GitHub.Copilot;

namespace Factory.Workbench.Worker;

#pragma warning disable GHCP001
internal static class CopilotModelAuthentication
{
    internal static async Task<GitHubTokenProviderResult> TokenAsync(string host, string model,
        Func<CancellationToken, Task<ModelAccessCredential>> acquire, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Copilot model credentials can only be used with github.com.");
        var credential = await acquire(cancellationToken);
        var now = clock.GetUtcNow();
        credential.Validate(model, now);
        return GitHubTokenProviderResult.FromToken(new GitHubToken
        {
            AccessToken = credential.AccessToken,
            ExpiresIn = (int)Math.Floor((credential.ExpiresAt - now).TotalSeconds)
        });
    }
    #pragma warning restore GHCP001
}
