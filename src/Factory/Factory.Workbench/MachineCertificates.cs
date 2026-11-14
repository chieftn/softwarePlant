using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Factory.Workbench;

[SupportedOSPlatform("windows")]
public static class MachineCertificates
{
    public static void ProtectStateDirectory(string root)
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
        for (var current = directory; current is not null &&
            !current.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(boundary.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase); current = current.Parent)
        {
            if (!current.Exists)
                continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
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
        if (directory.Exists)
        {
            if (File.Exists(Path.Combine(root, "config.json")))
                ConfigurationStore.Read(root);
            else if (Directory.EnumerateFileSystemEntries(root).Any(path =>
                Path.GetFileName(path) != "maintenance.lock" && Path.GetFileName(path) != "runtime") ||
                Directory.Exists(LocalState.RuntimeDirectory(root)) &&
                Directory.EnumerateFileSystemEntries(LocalState.RuntimeDirectory(root)).Any(path =>
                    Path.GetFileName(path) is not ("runtime.lock" or "plant-reports.lock")))
                throw new InvalidDataException("A nonempty state directory has no valid configuration. Restore/reconcile it; do not generate another installation identity.");
        }
        directory.Create();
        var protectedAcl = new DirectorySecurity();
        protectedAcl.SetAccessRuleProtection(true, false);
        protectedAcl.SetOwner(administrators);
        foreach (var principal in new[] { administrators, system })
            protectedAcl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (FileSystemAccessRule rule in directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.IdentityReference.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) && rule.AccessControlType == AccessControlType.Allow)
                protectedAcl.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(protectedAcl);

        bool Trusted(SecurityIdentifier principal) => principal == administrators || principal == system || principal == identity.User;
    }

    public static void GrantServiceStateAccess(string root, WorkbenchInstallation installation)
    {
        ProtectStateDirectory(root);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = (SecurityIdentifier)new NTAccount(installation.ServiceAccount).Translate(typeof(SecurityIdentifier));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(administrators);
        foreach (var principal in new[] { administrators, system })
            acl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
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
        if ((runtime.Attributes & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(runtime.FullName).Any(path =>
                (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new InvalidDataException("Runtime state must contain only regular files, not nested directories or links.");
        var runtimeAcl = new DirectorySecurity();
        runtimeAcl.SetAccessRuleProtection(true, false);
        runtimeAcl.SetOwner(administrators);
        foreach (var principal in new[] { administrators, system })
            runtimeAcl.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        runtimeAcl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        runtime.SetAccessControl(runtimeAcl);
    }

    public static X509Certificate2 GetOrCreate(string root)
    {
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
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey, KeyUsage = CngKeyUsages.Signing,
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
