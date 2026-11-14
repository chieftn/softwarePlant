using System.IO.Compression;
using System.Net;
using Factory.Workbench;
using Xunit;

namespace Factory.Tests;

public sealed class SetupDiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"factory-diagnostics-tests-{Guid.NewGuid():N}");

    [Fact]
    public void FailureTraceExcludesExceptionMessagesAndExportExcludesCredentialState()
    {
        var root = Path.Combine(directory, "state");
        var diagnostics = new SetupDiagnostics(root);
        diagnostics.Record("enrollment", "failed", new PlantConnectionException(HttpStatusCode.Forbidden,
            "Bearer fixture-secret access_token=another-secret"));
        File.WriteAllText(Path.Combine(root, "binding.json"), "fixture-private-state");
        var zip = Path.Combine(directory, "diagnostics.zip");
        diagnostics.Export(zip, root);
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, entry => entry.Name == "binding.json");
        using var reader = new StreamReader(Assert.Single(archive.Entries, entry => entry.Name.EndsWith(".jsonl")).Open());
        var contents = reader.ReadToEnd();
        Assert.Contains("403", contents);
        Assert.DoesNotContain("fixture-secret", contents);
        Assert.DoesNotContain("another-secret", contents);
        Assert.Throws<InvalidDataException>(() => diagnostics.Export(Path.Combine(root, "lost-on-uninstall.zip"), root));
    }

    [Fact]
    public void ExportIncludesRedactedMsiLogWithoutCopyingConfiguration()
    {
        var root = Path.Combine(directory, "state");
        var diagnostics = new SetupDiagnostics(root);
        var msi = Path.Combine(directory, "installer.log");
        File.WriteAllText(msi, "Action failed 1603\nBearer fixture-token\npassword=fixture-password");
        var zip = Path.Combine(directory, "diagnostics.zip");
        diagnostics.Export(zip, root, msi);
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("windows-installer.log")!.Open());
        var contents = reader.ReadToEnd();
        Assert.Contains("1603", contents);
        Assert.DoesNotContain("fixture-token", contents);
        Assert.DoesNotContain("fixture-password", contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
