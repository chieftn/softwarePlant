using Microsoft.OpenApi.Readers;
using Xunit;

namespace Factory.Tests;

public sealed class PlantApiContractTests
{
    [Fact]
    public void ContractParsesAndPassesOpenApiValidation()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "plant.openapi.yaml"));
        var document = new OpenApiStreamReader().Read(stream, out var diagnostic);

        Assert.NotNull(document);
        Assert.True(diagnostic.Errors.Count == 0, string.Join(Environment.NewLine, diagnostic.Errors));
        Assert.True(diagnostic.Warnings.Count == 0, string.Join(Environment.NewLine, diagnostic.Warnings));
    }
}
