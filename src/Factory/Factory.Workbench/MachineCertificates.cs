using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Factory.Workbench;

[SupportedOSPlatform("windows")]
public static class MachineCertificates
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public static void ValidateInstallerExecutable(string executable, SetupDiagnostics? diagnostics = null)
    {
        var programFiles = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(executable);
        Check("helper-path", () =>
        {
            if (!path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException("The privileged installer helper must be installed beneath protected Program Files.");
        });
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        for (FileSystemInfo? item = new FileInfo(path); item is not null &&
            !Path.TrimEndingDirectorySeparator(item.FullName).Equals(
                Path.TrimEndingDirectorySeparator(programFiles), StringComparison.OrdinalIgnoreCase);
            item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
        {
            var scope = item is FileInfo ? "helper-file" : "helper-directory";
            Check($"{scope}-link", () =>
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The privileged installer helper must not traverse links.");
            });
            Check($"{scope}-acl", () =>
            {
                FileSystemSecurity acl = item is FileInfo fileInfo ? fileInfo.GetAccessControl() : ((DirectoryInfo)item).GetAccessControl();
                Check($"{scope}-owner", () =>
                {
                    if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                        owner != administrators && owner != system)
                        throw new UnauthorizedAccessException("Installer helper ownership must be Administrators or SYSTEM.");
                });
                Check($"{scope}-write-permissions", () =>
                {
                    foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        if (HasUntrustedInstallerWrite(rule))
                            throw new UnauthorizedAccessException("The installer helper permits untrusted modification.");
                });
            });
        }

        void Check(string stage, Action operation)
        {
            if (diagnostics is null)
                operation();
            else
                diagnostics.RunStage(stage, operation);
        }
    }

    internal static bool HasUntrustedInstallerWrite(FileSystemAccessRule rule)
    {
        var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;
        return rule.AccessControlType == AccessControlType.Allow &&
            (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
            (rule.FileSystemRights & writes) != 0 &&
            rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544" or TrustedInstallerSid);
    }

    public static void ProtectStateDirectory(string root, bool allowRemovalRecovery = false)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        using var identity = WindowsIdentity.GetCurrent();
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        var common = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var boundary = (directory.FullName.Equals(common, StringComparison.OrdinalIgnoreCase) ||
            directory.FullName.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ? common : Path.GetPathRoot(directory.FullName)!;
        if (directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(
            boundary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The state directory must not be a filesystem or shared-data root.");
        var missing = new Stack<DirectoryInfo>();
        for (var current = directory; current is not null &&
            !current.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(boundary.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase); current = current.Parent)
        {
            if (!current.Exists)
            {
                missing.Push(current);
                continue;
            }
            ValidateAncestor(current);
        }
        if (directory.Exists)
        {
            if (File.Exists(Path.Combine(root, "config.json")))
                ConfigurationStore.Read(root);
            else if (allowRemovalRecovery && File.Exists(Path.Combine(root, "removal-intent.json")))
            {
                var removal = LocalState.Read<RemovalIntent>(Path.Combine(root, "removal-intent.json"));
                if (removal.SchemaVersion != "1" || !removal.CertificatesRemoved ||
                    !Guid.TryParseExact(removal.InstallationId, "D", out var id) || id == Guid.Empty)
                    throw new InvalidDataException("Removal recovery requires verified prior certificate cleanup.");
            }
            else if (Directory.EnumerateFileSystemEntries(root).Any(path =>
                Path.GetFileName(path) is not ("maintenance.lock" or "runtime" or "logs")) ||
                Directory.Exists(LocalState.RuntimeDirectory(root)) &&
                Directory.EnumerateFileSystemEntries(LocalState.RuntimeDirectory(root)).Any(path =>
                    Path.GetFileName(path) is not ("runtime.lock" or "plant-reports.lock")))
                throw new InvalidDataException("A nonempty state directory has no valid configuration. Restore/reconcile it; do not generate another installation identity.");
            if (Directory.Exists(Path.Combine(root, "logs")))
                SetupDiagnostics.ValidateDirectory(Path.Combine(root, "logs"));
        }
        var protectedAcl = CreateStateDirectoryAcl();
        while (missing.TryPop(out var current))
        {
            protectedAcl.CreateDirectory(current.FullName);
            current.Refresh();
            ValidateAncestor(current);
        }
        directory.Refresh();
        foreach (FileSystemAccessRule rule in directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.IdentityReference.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) && rule.AccessControlType == AccessControlType.Allow)
                protectedAcl.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(protectedAcl);

        void ValidateAncestor(DirectoryInfo current)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                throw new InvalidDataException("State directories must not traverse filesystem links.");
            var acl = current.GetAccessControl();
            if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                owner != administrators && owner != system && owner != identity.User)
                throw new UnauthorizedAccessException("Existing state-directory ownership is not trusted. An administrator must reconcile it.");
            var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    (rule.FileSystemRights & writes) != 0 && !Trusted((SecurityIdentifier)rule.IdentityReference))
                    throw new UnauthorizedAccessException("Existing state directories allow untrusted writes. An administrator must reconcile them.");
        }

        bool Trusted(SecurityIdentifier principal) => principal == administrators || principal == system || principal == identity.User;
    }

    internal static DirectorySecurity CreateStateDirectoryAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return acl;
    }

    public static void GrantServiceStateAccess(string root, WorkbenchInstallation installation)
    {
        ValidateServiceRuntimeState(root);
        ProtectStateDirectory(root);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = (SecurityIdentifier)new NTAccount(installation.ServiceAccount).Translate(typeof(SecurityIdentifier));
        var acl = CreateStateDirectoryAcl();
        acl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        foreach (var path in Directory.EnumerateFiles(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer-owned state files must not be links.");
            var fileAcl = new FileSecurity();
            fileAcl.SetAccessRuleProtection(true, false);
            fileAcl.SetOwner(administrators);
            foreach (var principal in new[] { administrators, system })
                fileAcl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, AccessControlType.Allow));
            fileAcl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileAcl);
        }
        var runtime = Directory.CreateDirectory(LocalState.RuntimeDirectory(root));
        var runtimeAcl = new DirectorySecurity();
        runtimeAcl.SetAccessRuleProtection(true, false);
        runtimeAcl.SetOwner(administrators);
        foreach (var principal in new[] { administrators, system })
            runtimeAcl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        runtimeAcl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        runtime.SetAccessControl(runtimeAcl);
        var telemetry = new DirectoryInfo(MissionTelemetry.DirectoryPath(root));
        if (telemetry.Exists)
        {
            telemetry.SetAccessControl(runtimeAcl);
            foreach (var path in Directory.EnumerateFiles(telemetry.FullName))
            {
                var fileAcl = new FileSecurity();
                fileAcl.SetAccessRuleProtection(true, false);
                fileAcl.SetOwner(administrators);
                foreach (var principal in new[] { administrators, system })
                    fileAcl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, AccessControlType.Allow));
                fileAcl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(fileAcl);
            }
        }
    }

    public static void ValidateServiceRuntimeState(string root)
    {
        var runtime = LocalState.RuntimeDirectory(root);
        MissionTelemetry.RequireRegularPath(Path.Combine(runtime, "validation"));
        if (!Directory.Exists(runtime))
            return;
        var telemetry = MissionTelemetry.DirectoryPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(runtime))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime state must not contain filesystem links.");
            if ((attributes & FileAttributes.Directory) == 0)
            {
                if (path.Equals(telemetry, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mission telemetry storage must be a directory, not a file.");
                continue;
            }
            if (!path.Equals(telemetry, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFileSystemEntries(path).Any(child =>
                    (File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
                throw new InvalidDataException("Runtime state permits only regular files and the flat owned mission-telemetry directory; no other directories or links.");
        }
    }

    public static void GrantServiceLinuxAccess(string root, WorkbenchInstallation installation)
    {
        var path = WslWorkerEnvironment.EnvironmentDirectory(root);
        if (File.Exists(path) || Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linux environment storage cannot be a file or link.");
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var service = (SecurityIdentifier)new NTAccount(installation.ServiceAccount).Translate(typeof(SecurityIdentifier));
        acl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    public static X509Certificate2 GetOrCreate(string root)
    {
        BindingStore.RequireEnrollmentAllowed(root);
        var configuration = ConfigurationStore.Read(root);
        if (File.Exists(Path.Combine(root, "binding.json")))
            return Read(BindingStore.Read(root).CertificateThumbprint);
        if (File.Exists(Path.Combine(root, "enrollment.json")))
            return Read(LocalState.Read<EnrollmentJournal>(Path.Combine(root, "enrollment.json")).CertificateThumbprint);
        var name = $"Factory.Workbench.{Guid.Parse(configuration.InstallationId):N}";
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Cast<X509Certificate2>().Where(cert => cert.FriendlyName == name).ToArray();
        if (existing.Length > 1)
            throw new InvalidDataException("Multiple machine certificates exist; reconcile rather than generating another identity.");
        if (existing.Length == 1)
            return existing[0];
        using var key = CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)
            ? CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)
            : CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                KeyUsage = CngKeyUsages.Signing,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) }
            });
        using var rsa = new RSACng(key);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(180));
        certificate.FriendlyName = name;
        store.Add(certificate);
        var saved = Read(certificate.Thumbprint);
        SetKeyAccess(saved, null);
        return saved;
    }

    public static X509Certificate2 Read(string thumbprint)
    {
        if (thumbprint.Length != 40 || !thumbprint.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid machine certificate thumbprint.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count != 1 || !matches[0].HasPrivateKey)
            throw new InvalidDataException("The machine certificate/private key is unavailable. Restore it; do not re-enroll automatically.");
        return matches[0];
    }

    public static void Remove(string root, string thumbprint)
    {
        if (thumbprint.Length != 40 || !thumbprint.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid machine certificate thumbprint.");
        var configuration = ConfigurationStore.Read(root);
        var name = $"Factory.Workbench.{Guid.Parse(configuration.InstallationId):N}";
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count > 1 || matches.Count == 1 && matches[0].FriendlyName != name)
            throw new InvalidDataException("Refusing to remove a certificate not owned by this workbench.");
        if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey))
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
            using var rsa = new RSACng(key);
            if (matches.Count == 1)
            {
                using var publicKey = matches[0].GetRSAPublicKey()
                    ?? throw new InvalidDataException("The enrolled certificate is not an RSA certificate.");
                if (!rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
                    throw new InvalidDataException("The named private key does not match the enrolled certificate.");
            }

            key.Delete();
        }
        foreach (var certificate in matches)
        {
            using (certificate)
                store.Remove(certificate);
        }
    }

    public static void RemoveOwned(string root)
    {
        var configuration = ConfigurationStore.Read(root);
        var name = $"Factory.Workbench.{Guid.Parse(configuration.InstallationId):N}";
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Cast<X509Certificate2>().Where(cert => cert.FriendlyName == name).ToArray();
        try
        {
            if (matches.Length > 1)
                throw new InvalidDataException("Multiple owned certificates require reconciliation before full removal.");
            var journal = File.Exists(Path.Combine(root, "enrollment.json")) ? BindingStore.ReadJournal(root) : null;
            var binding = File.Exists(Path.Combine(root, "binding.json")) ? BindingStore.Read(root) : null;
            if (journal is not null && binding is not null && journal.CertificateThumbprint != binding.CertificateThumbprint)
                throw new InvalidDataException("Enrollment and binding certificates disagree.");
            var thumbprint = binding?.CertificateThumbprint ?? journal?.CertificateThumbprint ?? matches.SingleOrDefault()?.Thumbprint;
            if (matches.Length == 1 && thumbprint != matches[0].Thumbprint)
                throw new InvalidDataException("The enrollment journal does not match the owned certificate.");
            Remove(root, thumbprint ?? new string('0', 40));
        }
        finally
        {
            foreach (var certificate in matches)
                certificate.Dispose();
        }
    }

    public static void GrantServiceAccess(string root, WorkbenchInstallation installation)
    {
        if (!File.Exists(Path.Combine(root, "binding.json")))
            return;
        var binding = BindingStore.Read(root);
        if (binding.InstallationId != installation.InstallationId)
            throw new InvalidDataException("Certificate binding belongs to a different installation.");
        using var certificate = Read(binding.CertificateThumbprint);
        SetKeyAccess(certificate, installation.ServiceAccount);
    }

    private static void SetKeyAccess(X509Certificate2 certificate, string? serviceAccount)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not RSACng cng || !cng.Key.IsMachineKey || cng.Key.UniqueName is not { Length: > 0 } keyName ||
            Path.GetFileName(keyName) != keyName)
            throw new InvalidDataException("Factory requires a persistent, machine-scoped CNG RSA signing key.");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Crypto", "Keys", keyName);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        if (serviceAccount is not null)
            security.AddAccessRule(new FileSystemAccessRule(serviceAccount, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
