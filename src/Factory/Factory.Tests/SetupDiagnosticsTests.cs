using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
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
        LocalState.Write(WslWorkerEnvironment.StatusPath(root), new WorkerSetupStatus(Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), "test-release", 1, DateTimeOffset.UtcNow, "failed", "worker-sdk-check",
            "SDK startup failed. Bearer fixture-worker-token password=fixture-worker-password"));
        var zip = Path.Combine(directory, "diagnostics.zip");
        diagnostics.Export(zip, root, msi);
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("windows-installer.log")!.Open());
        var contents = reader.ReadToEnd();
        Assert.Contains("1603", contents);
        Assert.DoesNotContain("fixture-token", contents);
        Assert.DoesNotContain("fixture-password", contents);
        using var workerReader = new StreamReader(archive.GetEntry("worker-setup.json")!.Open());
        var worker = workerReader.ReadToEnd();
        Assert.Contains("worker-sdk-check", worker);
        Assert.DoesNotContain("fixture-worker-token", worker);
        Assert.DoesNotContain("fixture-worker-password", worker);
    }

    [Fact]
    public void FailedValidationStagePersistsCauseWithoutExceptionMessageAndRethrowsOriginal()
    {
        var diagnostics = new SetupDiagnostics(Path.Combine(directory, "state"));
        var original = new UnauthorizedAccessException("fixture-secret");
        var thrown = Assert.Throws<UnauthorizedAccessException>(() =>
            diagnostics.RunStage("helper-file-owner", () => throw original));
        Assert.Same(original, thrown);
        var contents = File.ReadAllText(diagnostics.FilePath);
        var entries = File.ReadLines(diagnostics.FilePath)
            .Select(line => JsonSerializer.Deserialize<SetupDiagnostic>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        var failed = Assert.Single(entries, entry => entry.Outcome == "failed");
        Assert.Equal("helper-file-owner", failed.Stage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, failed.ErrorType);
        Assert.Equal(original.HResult, failed.ErrorCode);
        Assert.DoesNotContain(entries, entry => entry.Outcome == "completed");
        Assert.DoesNotContain("fixture-secret", contents);
    }

    [Fact]
    public void FailureToWriteValidationTraceDoesNotReplaceOriginalValidationFailure()
    {
        var diagnostics = new SetupDiagnostics(Path.Combine(directory, "state"));
        var original = new UnauthorizedAccessException("fixture-secret");
        var thrown = Assert.Throws<UnauthorizedAccessException>(() =>
            diagnostics.RunStage("helper-file-owner", () =>
            {
                File.WriteAllText(Path.Combine(diagnostics.DirectoryPath, "unexpected-file"), "");
                throw original;
            }));
        Assert.Same(original, thrown);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void StateDirectoryReadAccessDoesNotInheritIntoCredentialFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var acl = MachineCertificates.CreateStateDirectoryAcl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            entry => entry.IdentityReference.Equals(users));
        Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
        Assert.Equal((FileSystemRights)0, rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions));
        Assert.True(acl.AreAccessRulesProtected);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void DiagnosticBrowsingGrantsReadWithoutGrantingGroupWriteOrPublishingOtherFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(directory, "state");
        Directory.CreateDirectory(root);
        var privateFile = Path.Combine(root, "credential-fixture");
        File.WriteAllText(privateFile, "fixture-secret");
        var previousAcl = new FileInfo(privateFile).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var diagnostics = new SetupDiagnostics(root);
        diagnostics.Record("helper-path", "started");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (var acl in new FileSystemSecurity[]
        {
            new DirectoryInfo(diagnostics.DirectoryPath).GetAccessControl(), new FileInfo(diagnostics.FilePath).GetAccessControl()
        })
        {
            var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                entry => entry.IdentityReference.Equals(users));
            Assert.Equal((FileSystemRights)0, rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions));
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
        }
        Assert.Equal(previousAcl,
            new FileInfo(privateFile).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void InheritedWindowsServicingDirectoryAccessIsNotAnUntrustedWriter()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm(
            "O:BAG:SYD:AI(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;BU)" +
            "(A;CIID;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)");
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.DoesNotContain(rules, MachineCertificates.HasUntrustedInstallerWrite);
    }

    [Theory]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [InlineData("FA")]
    [InlineData("GA")]
    [InlineData("GW")]
    public void OrdinaryUsersRemainUntrustedForSpecificAndGenericWriteAccess(string rights)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm($"D:(A;;{rights};;;BU)");
        var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.True(MachineCertificates.HasUntrustedInstallerWrite(rule));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void UnrelatedServiceWriterRemainsUntrusted()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var rule = new FileSystemAccessRule(new SecurityIdentifier("S-1-5-80-1-2-3-4-5"),
            FileSystemRights.FullControl, AccessControlType.Allow);
        Assert.True(MachineCertificates.HasUntrustedInstallerWrite(rule));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ReadAccessAndInheritOnlyTemplatesAreNotEffectiveWriters()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Assert.False(MachineCertificates.HasUntrustedInstallerWrite(
            new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow)));
        var read = new DirectorySecurity();
        read.SetSecurityDescriptorSddlForm("D:(A;;GRGX;;;BU)");
        Assert.False(MachineCertificates.HasUntrustedInstallerWrite(
            Assert.Single(read.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())));
        Assert.False(MachineCertificates.HasUntrustedInstallerWrite(
            new FileSystemAccessRule(users, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly,
                AccessControlType.Allow)));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
