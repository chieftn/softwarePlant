using Factory.Workbench;
using Xunit;

namespace Factory.Tests;

public sealed class RuntimeTelemetryAccessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factory-runtime-access-{Guid.NewGuid():N}");

    [Fact]
    public void OwnedTelemetryDirectoryAndFlatRuntimeFilesAreAcceptedWithoutDeletingEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return;
        LocalState.Write(LocalState.RuntimeStatusPath(root), new { state = "stopped" });
        var telemetry = MissionTelemetry.DirectoryPath(root);
        Directory.CreateDirectory(telemetry);
        var path = Path.Combine(telemetry, "mission-evidence.jsonl");
        File.WriteAllText(path, "retained evidence");

        MachineCertificates.ValidateServiceRuntimeState(root);

        Assert.Equal("retained evidence", File.ReadAllText(path));
        Assert.True(File.Exists(LocalState.RuntimeStatusPath(root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedRuntimeDirectoriesAndNestedTelemetryDirectoriesAreRejected(bool nested)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(nested
            ? Path.Combine(MissionTelemetry.DirectoryPath(root), "unexpected")
            : Path.Combine(LocalState.RuntimeDirectory(root), "unexpected"));
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                MachineCertificates.ValidateServiceRuntimeState(root);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
