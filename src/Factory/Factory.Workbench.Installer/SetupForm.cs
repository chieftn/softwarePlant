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
    private readonly ComboBox plants = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 680 };
    private readonly Button signIn = new() { Text = "1. Sign in", AutoSize = true };
    private readonly Button install = new() { Text = "2. Enroll and start service", AutoSize = true, Enabled = false };
    private readonly FlowLayoutPanel actions = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowsWorkbenchService service = new();
    private WorkbenchInstallationProfile? profile;
    private EnrollmentClient? enrollment;
    private HttpClient? http;
    private bool busy;
    private bool closing;
    private bool configurationReady;

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
        layout.Controls.Add(new Label { Text = "Install, enroll, and connect this workbench", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(profileLabel);
        layout.Controls.Add(new Label
        {
            Text = "Development build: install only from a trusted source. Enrollment requires a plant owner or plant-scoped registrar.\n" +
                "The service starts at boot without sign-in. Worker execution and WSL remain disabled.",
            AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 15, 0, 15)
        });
        layout.Controls.Add(signIn);
        layout.Controls.Add(plants);
        layout.Controls.Add(install);
        AddAction("Start service", () => Lifecycle.StartAsync(lifetime.Token));
        AddAction("Stop service", () => Lifecycle.StopAsync(lifetime.Token));
        AddAction("Recover service", () => Lifecycle.RecoverAsync(lifetime.Token));
        AddAction("Roll back runtime", () => Lifecycle.RollbackAsync(lifetime.Token));
        layout.Controls.Add(actions);
        layout.Controls.Add(status);
        Controls.Add(layout);
        signIn.Click += async (_, _) => await Run(SignInAsync);
        install.Click += async (_, _) => await Run(InstallAsync);
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
        configurationReady = true;
        status.Text = "Sign in, select your plant, and enroll this machine.";
        if (File.Exists(Path.Combine(root, "binding.json")))
        {
            var binding = BindingStore.Read(root);
            install.Text = "Repair installation and start service";
            status.Text = $"Machine identity retained. Plant {binding.PlantId}; workbench {binding.WorkbenchId}. No human sign-in is needed to reinstall.";
        }
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
        var available = await connected.PlantsAsync(lifetime.Token);
        plants.DisplayMember = nameof(PlantView.Name);
        plants.DataSource = available.ToArray();
        if (File.Exists(Path.Combine(root, "enrollment.json")))
        {
            var journal = LocalState.Read<EnrollmentJournal>(Path.Combine(root, "enrollment.json"));
            plants.SelectedItem = available.SingleOrDefault(item => item.Id == journal.PlantId)
                ?? throw new InvalidDataException("The enrolled plant is not accessible to this account. Resume with its authorized owner/registrar.");
            plants.Enabled = false;
        }
        enrollment = connected;
        status.Text = available.Count == 0 ? "No active plants are accessible to this account." : "Select your plant, then enroll and start the service.";
    }

    private async Task InstallAsync()
    {
        EnsureConfiguration();
        if (!File.Exists(Path.Combine(root, "binding.json")))
        {
            var client = enrollment ?? throw new InvalidDataException("Sign in before enrolling.");
            var plant = plants.SelectedItem as PlantView ?? throw new InvalidDataException("Select a plant.");
            using var maintenance = LocalState.Lock(root, "maintenance.lock");
            using var ownership = LocalState.LockRuntime(root);
            using var certificate = MachineCertificates.GetOrCreate(root);
            var journal = BindingStore.Journal(root, plant.Id, certificate);
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
        status.Text = $"Connected: {binding.WorkbenchId}. The website now shows server-observed registration/presence.\n" +
            "The Windows Service will reconnect after reboot without user sign-in. Execution remains notReady; WSL is not started.";
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
        signIn.Enabled = install.Enabled = actions.Enabled = false;
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
            install.Enabled = configurationReady && (File.Exists(Path.Combine(root, "binding.json")) ||
                enrollment is not null && plants.SelectedItem is PlantView);
            actions.Enabled = configurationReady && File.Exists(Path.Combine(root, "installation.json"));
            if (closing)
                Close();
        }
    }
}
