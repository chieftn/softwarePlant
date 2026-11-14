using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench.Setup;
using Microsoft.Identity.Client;

namespace Factory.Workbench.Installer;

internal sealed class SetupForm : Form
{
    private readonly string root = ConfigurationStore.DefaultRoot;
    private readonly string payload = Path.Combine(AppContext.BaseDirectory, "payload");
    private readonly Label profileLabel = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(700, 0), Text = "Loading installation profile..." };
    private readonly Button signIn = new() { Text = "1. Sign in", AutoSize = true };
    private readonly Button install = new() { Text = "2. Enroll and start service", AutoSize = true, Enabled = false };
    private readonly Button unenroll = new() { Text = "Unenroll and remove service", AutoSize = true, Enabled = false };
    private readonly FlowLayoutPanel actions = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowsWorkbenchService service = new();
    private WorkbenchInstallationProfile? profile;
    private EnrollmentClient? enrollment;
    private HttpClient? http;
    private bool busy;
    private bool closing;
    private bool configurationReady;
    private bool removalPending;
    private bool localCleanupVerified;

    public SetupForm()
    {
        Text = "Factory Workbench Setup";
        ClientSize = new Size(760, 440);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(20), AutoScroll = true
        };
        layout.Controls.Add(new Label { Text = "Install and enroll this device in Factory", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(profileLabel);
        layout.Controls.Add(new Label
        {
            Text = "Development build: install only from a trusted source. Enrollment requires Factory.EnrollmentAdministrator.\n" +
                "Assign the enrolled device to a plant through the website. Worker execution and WSL remain disabled.",
            AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 15, 0, 15)
        });
        layout.Controls.Add(signIn);
        layout.Controls.Add(install);
        layout.Controls.Add(unenroll);
        AddAction("Start service", () => Lifecycle.StartAsync(lifetime.Token));
        AddAction("Stop service", () => Lifecycle.StopAsync(lifetime.Token));
        AddAction("Recover service", () => Lifecycle.RecoverAsync(lifetime.Token));
        AddAction("Roll back runtime", () => Lifecycle.RollbackAsync(lifetime.Token));
        layout.Controls.Add(actions);
        layout.Controls.Add(status);
        Controls.Add(layout);
        signIn.Click += async (_, _) => await Run(SignInAsync);
        install.Click += async (_, _) => await Run(InstallAsync);
        unenroll.Click += async (_, _) => await Run(UnenrollAsync);
        Shown += async (_, _) => await Run(LoadProfileAsync);
        FormClosing += (_, e) =>
        {
            lifetime.Cancel();
            if (busy)
            {
                closing = true;
                e.Cancel = true;
                status.Text = "Canceling. Enrollment progress and machine identity will be retained.";
            }
        };
        FormClosed += (_, _) => { http?.Dispose(); lifetime.Dispose(); };
    }

    private WorkbenchLifecycle Lifecycle => new(root, service);

    private Task LoadProfileAsync()
    {
        profile = LocalState.Read<WorkbenchInstallationProfile>(Path.Combine(AppContext.BaseDirectory, "installation-profile.json"));
        profile.Validate();
        profileLabel.Text = $"Plant service: {profile.PlantServiceUrl}\nTenant: {profile.TenantId}";
        MachineCertificates.ProtectStateDirectory(root);
        EnsureConfiguration();
        status.Text = "Sign in and enroll this device. Plant assignment happens in the Factory website.";
        if (File.Exists(Path.Combine(root, "binding.json")))
        {
            var binding = BindingStore.Read(root);
            install.Text = "Repair installation and start service";
            status.Text = $"Machine identity retained. Workbench {binding.WorkbenchId}. No human sign-in is needed to reinstall.";
        }
        var removal = BindingStore.Removal(root);
        removalPending = removal is { State: not "completed" };
        localCleanupVerified = removal?.State == "retired";
        if (removalPending)
            status.Text = "Unenrollment is incomplete. Resume Unenroll; starting or re-enrolling is blocked.";
        configurationReady = true;
        return Task.CompletedTask;
    }

    private void EnsureConfiguration()
    {
        var trusted = profile ?? throw new InvalidDataException("Installation profile is unavailable.");
        using var maintenance = LocalState.Lock(root, "maintenance.lock");
        if (File.Exists(Path.Combine(root, "config.json")))
        {
            var existing = ConfigurationStore.Read(root);
            if (existing.PlantServiceUrl != trusted.PlantServiceUrl || existing.TenantId != trusted.TenantId ||
                existing.ApiClientId != trusted.ApiClientId || existing.InstallerClientId != trusted.InstallerClientId)
                throw new InvalidDataException("This machine has a different installation profile. Do not overwrite or re-enroll its identity.");
            return;
        }
        ConfigurationStore.Save(root, new WorkbenchConfiguration
        {
            SchemaVersion = "1", InstallationId = Guid.NewGuid().ToString("D"),
            PlantServiceUrl = trusted.PlantServiceUrl, TenantId = trusted.TenantId,
            ApiClientId = trusted.ApiClientId, InstallerClientId = trusted.InstallerClientId,
            Distribution = "Ubuntu", LinuxUser = "worker", LinuxWorkspaceRoot = "/home/worker/work",
            AllowedRepositories = [], AllowShellExecution = false, AllowPublication = false
        });
    }

    private async Task SignInAsync()
    {
        var trusted = profile ?? throw new InvalidDataException("Installation profile is unavailable.");
        var scopes = new[] { $"api://{trusted.ApiClientId}/Plant.Access" };
        var application = PublicClientApplicationBuilder.Create(trusted.InstallerClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, trusted.TenantId).WithRedirectUri("http://localhost").Build();
        status.Text = "Complete Entra sign-in in your browser.";
        var authentication = await application.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount).ExecuteAsync(lifetime.Token);
        http?.Dispose();
        http = PlantHttp.Create(trusted.PlantServiceUrl);
        var connected = new EnrollmentClient(new PlantHttp(http, async cancellation =>
            (await application.AcquireTokenSilent(scopes, authentication.Account).ExecuteAsync(cancellation)).AccessToken));
        await connected.InventoryAsync(lifetime.Token);
        enrollment = connected;
        status.Text = "Enrollment administrator authorized. Enroll and start the service; assign this device through the website afterward.";
    }

    private async Task InstallAsync()
    {
        BindingStore.RequireEnrollmentAllowed(root);
        EnsureConfiguration();
        if (!File.Exists(Path.Combine(root, "binding.json")))
        {
            var client = enrollment ?? throw new InvalidDataException("Sign in before enrolling.");
            using var maintenance = LocalState.Lock(root, "maintenance.lock");
            using var ownership = LocalState.LockRuntime(root);
            using var certificate = MachineCertificates.GetOrCreate(root);
            var journal = BindingStore.Journal(root, certificate);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var view = await client.SubmitAsync(journal, certificate, deadline.Token);
            while (view.State is "authorized" or "provisioning")
            {
                status.Text = $"Enrollment: {view.State}. {view.Error}\nOperation {journal.OperationId}. Closing retains progress for resume.";
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                view = await client.StatusAsync(journal, deadline.Token);
            }
            if (view.State is not ("ready" or "active"))
                throw new InvalidDataException($"Enrollment {view.State}: {view.Error} Resume this operation; do not generate a replacement identity.");
            EnrollmentClient.SaveBinding(root, journal, view);
        }
        var binding = BindingStore.Read(root);
        var configuration = ConfigurationStore.Read(root);
        using (var certificate = MachineCertificates.Read(binding.CertificateThumbprint))
        using (var machineHttp = PlantHttp.Create(configuration.PlantServiceUrl))
        {
            var token = new CertificateTokenSource(configuration, binding, certificate);
            var connection = new WorkbenchPlantConnection(new PlantHttp(machineHttp, token.TokenAsync, token.RequestFreshToken), binding,
                PackageInstaller.ReadManifest(payload).ReleaseId, root);
            status.Text = "Verifying the new machine identity with Entra and the plant...";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                try { await connection.PublishAsync(deadline.Token); break; }
                catch (Exception error) when (!deadline.IsCancellationRequested &&
                    (error is MsalServiceException { ErrorCode: "invalid_client" or "unauthorized_client" } ||
                     error is PlantConnectionException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }))
                {
                    status.Text = "Waiting for directory credential/role propagation. This is bounded to five minutes.";
                    await Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
                }
            }
        }
        status.Text = "Installing/configuring the dedicated Windows Service...";
        if (!File.Exists(Path.Combine(root, "installation.json")) || LocalState.ReadInstallation(root).State == "uninstalled")
            await Lifecycle.InstallAsync(payload, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Factory", "Workbench"), true, lifetime.Token);
        else
        {
            await Lifecycle.RecoverAsync(lifetime.Token);
            if (LocalState.ReadInstallation(root).ActiveRelease != PackageInstaller.ReadManifest(payload).ReleaseId)
                await Lifecycle.UpdateAsync(payload, true, lifetime.Token);
        }
        var started = DateTimeOffset.UtcNow;
        await Lifecycle.StartAsync(lifetime.Token);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(LocalState.RuntimeStatusPath(root)))
                {
                    var observed = LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(root));
                    if (observed.ObservedAt >= started && observed.PlantConnected &&
                        service.RunningProcessId(LocalState.ReadInstallation(root)) == observed.ProcessId)
                        break;
                    status.Text = "Waiting for service-account authentication: " + observed.Reason;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        status.Text = $"Enrolled and connected to Factory: {binding.WorkbenchId}.\n" +
            "Use Enrollments in the Factory website to assign this device to a plant. The service reconnects at boot; execution remains notReady.";
    }

    private async Task UnenrollAsync()
    {
        if (MessageBox.Show(this,
            "Revoke this enrollment, retire its dedicated Entra identity, remove the Windows service and delete its local certificate?\n\n" +
            "This cannot be undone. An interrupted attempt can be resumed. Re-enrollment creates a new identity.",
            "Unenroll workbench", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (enrollment is null && !localCleanupVerified)
            throw new InvalidDataException("Sign in as a Factory enrollment administrator before retiring this device.");
        removalPending = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        await new WorkbenchUnenrollment(root, service, MachineCertificates.Remove).RunAsync(enrollment,
            view => status.Text = $"Removal: {view.State}. {view.Error}\nOperation {view.Id}. Local identity is retained until retirement is confirmed.",
            deadline.Token);
        removalPending = false;
        localCleanupVerified = false;
        install.Text = "2. Enroll and start service";
        status.Text = "Unenrolled. The old identity is retired, the service and certificate are removed, and audit history is retained.\n" +
            "You may enroll again or uninstall the MSI. MSI uninstall alone does not revoke enrollment.";
    }

    private void AddAction(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => await Run(async () => { await action(); status.Text = text + " completed. Machine identity and journals retained."; });
        actions.Controls.Add(button);
    }

    private async Task Run(Func<Task> action)
    {
        if (busy)
            return;
        busy = true;
        signIn.Enabled = install.Enabled = unenroll.Enabled = actions.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException)
        { status.Text = "Canceled or timed out. Progress is retained. Resume setup with the same profile/account; do not delete identity or journals."; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ContractValidationException or MsalException or CryptographicException or
            InvalidOperationException or HttpRequestException or ArgumentException or System.ComponentModel.Win32Exception or AggregateException)
        {
            status.Text = "Setup failed: " + error.Message;
            MessageBox.Show(this, status.Text, "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            signIn.Enabled = configurationReady;
            install.Enabled = configurationReady && !removalPending && (File.Exists(Path.Combine(root, "binding.json")) ||
                enrollment is not null);
            unenroll.Enabled = configurationReady && (enrollment is not null || localCleanupVerified) &&
                (File.Exists(Path.Combine(root, "enrollment.json")) || removalPending);
            actions.Enabled = configurationReady && !removalPending && File.Exists(Path.Combine(root, "installation.json"));
            if (closing)
                Close();
        }
    }
}
