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
    private readonly Button unenroll = new() { Text = "Uninstall workbench...", AutoSize = true, Enabled = false };
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
    private readonly SetupDiagnostics diagnostics;

    public SetupForm(SetupDiagnostics diagnostics)
    {
        this.diagnostics = diagnostics;
        Text = "Factory Workbench Setup";
        ClientSize = new Size(760, 440);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20),
            AutoScroll = true
        };
        layout.Controls.Add(new Label { Text = "Install and enroll this device in Factory", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(profileLabel);
        layout.Controls.Add(new Label
        {
            Text = "Development build: install only from a trusted source. Enrollment requires Factory.EnrollmentAdministrator.\n" +
                "Assign the enrolled device to a plant and configure its production line through the website. " +
                "Prepare the SDK worker here, then select Start line to grant local execution permission.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 15, 0, 15)
        });
        layout.Controls.Add(signIn);
        layout.Controls.Add(install);
        layout.Controls.Add(unenroll);
        AddAction("Start service", () => Lifecycle.StartAsync(lifetime.Token));
        AddAction("Stop service", () => Lifecycle.StopAsync(lifetime.Token));
        AddAction("Recover service", () => Lifecycle.RecoverAsync(lifetime.Token));
        AddAction("Roll back runtime", () => Lifecycle.RollbackAsync(lifetime.Token));
        var prepareWorker = new Button { Text = "3. Prepare SDK worker...", AutoSize = true };
        prepareWorker.Click += async (_, _) => await Run(PrepareWorkerAsync, "worker-preparation");
        actions.Controls.Add(prepareWorker);
        var startLine = new Button { Text = "4. Start line...", AutoSize = true };
        startLine.Click += async (_, _) => await Run(StartLineAsync, "line-start");
        actions.Controls.Add(startLine);
        AddAction("Stop line", () => new WorkbenchExecutionSetup(root, service).StopAsync(lifetime.Token));
        var retryMission = new Button { Text = "Retry retained mission...", AutoSize = true };
        retryMission.Click += async (_, _) => await Run(RetryMissionAsync, "mission-retry");
        actions.Controls.Add(retryMission);
        layout.Controls.Add(actions);
        layout.Controls.Add(DiagnosticControls.Create(this, diagnostics, collectMissionDiagnostics: async () =>
        {
            status.Text = "Collecting retained mission diagnostics as the workbench service identity. No mission will be retried.";
            var collected = await new WorkbenchMissionDiagnosticSetup(root, service).CollectAsync(lifetime.Token);
            status.Text = collected?.Mission is { } mission
                ? $"Retained mission: {mission.State}; implementation passes: {mission.Iterations}; active time: {mission.ActiveMilliseconds / 1000}s.\n{mission.Reason}"
                : collected is null ? "No retained mission snapshot exists; exporting setup diagnostics only."
                : $"Retained mission diagnostics: {collected.State}.\n{collected.Detail}";
            return collected;
        }));
        layout.Controls.Add(status);
        Controls.Add(layout);
        signIn.Click += async (_, _) => await Run(SignInAsync, "sign-in");
        install.Click += async (_, _) => await Run(InstallAsync, "enrollment");
        unenroll.Click += async (_, _) => await Run(UnenrollAsync);
        Shown += async (_, _) => await Run(async () =>
        {
            await LoadProfileAsync();
            if (removalPending)
                return;
            if (File.Exists(Path.Combine(root, "binding.json")) &&
                File.Exists(Path.Combine(root, "installation.json")) &&
                LocalState.ReadInstallation(root) is { State: "installed", RunRequested: false })
            {
                status.Text = "Software updated. Existing enrollment and intentional service stop are preserved. " +
                    "Use Start service or Repair installation and start service when you want to reconnect.";
                return;
            }
            if (!File.Exists(Path.Combine(root, "binding.json")))
                await SignInAsync();
            await InstallAsync();
        }, "guided-install");
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
        if (removalPending)
            status.Text = "Unenrollment is incomplete. Resume Unenroll; starting or re-enrolling is blocked.";
        configurationReady = true;
        return Task.CompletedTask;
    }

    private void EnsureConfiguration()
    {
        var trusted = profile ?? throw new InvalidDataException("Installation profile is unavailable.");
        WorkbenchProfile.Configure(root, trusted);
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
        diagnostics.Record("sign-in", "authorized");
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
            diagnostics.Record("enrollment-submission", "started", enrollmentId: journal.OperationId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var view = await client.SubmitAsync(journal, certificate, deadline.Token);
            while (view.State is "authorized" or "provisioning")
            {
                status.Text = $"Enrollment: {view.State}. {view.Error}\nOperation {journal.OperationId}. Closing retains progress for resume.";
                diagnostics.Record("enrollment-provisioning", "polling", enrollmentId: journal.OperationId);
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                view = await client.StatusAsync(journal, deadline.Token);
            }
            if (view.State is not ("ready" or "active"))
                throw new InvalidDataException($"Enrollment {view.State}: {view.Error} Resume this operation; do not generate a replacement identity.");
            EnrollmentClient.SaveBinding(root, journal, view);
            diagnostics.Record("enrollment-submission", "bound", enrollmentId: journal.OperationId);
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
        await WaitForConnectionAsync(started);
        status.Text = $"Enrolled and connected to Factory: {binding.WorkbenchId}.\n" +
            "Use Enrollments in the Factory website to assign this device to a plant. Next: Prepare SDK worker.";
        diagnostics.Record("service-authentication", "connected", enrollmentId: binding.EnrollmentId);
        enrollment = null;
        http?.Dispose();
        http = null;
    }

    private async Task PrepareWorkerAsync()
    {
        if (MessageBox.Show(this,
            "Prepare this machine's dedicated Factory Linux environment?\n\n" +
            "Setup will install WSL if missing (Windows may require a restart), import the bundled Ubuntu image under " +
            "the existing service identity, download Linux dependencies, and check worker/SDK startup and shutdown.\n\n" +
            "Existing personal WSL distributions are not used or changed. No model call, issue claim or publication is started.",
            "Prepare SDK worker", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;
        var commands = new WslCommands();
        WslCommandResult? available = null;
        try { available = await commands.RunAsync(["--status"], null, lifetime.Token); }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 2)
        {
            throw new InvalidOperationException("Windows does not provide wsl.exe. Install the supported WSL Windows component before worker setup.", error);
        }
        if (available.ExitCode != 0)
        {
            status.Text = "Installing the WSL prerequisite. Windows may require a restart; enrollment will be retained.";
            var installed = await commands.RunAsync(["--install", "--no-distribution", "--web-download"], null, lifetime.Token);
            if (installed.ExitCode is not (0 or 3010))
                throw new IOException($"WSL installation failed ({installed.ExitCode}). {installed.Error} {installed.Output}");
            var check = await commands.RunAsync(["--status"], null, lifetime.Token);
            if (installed.ExitCode == 3010 || check.ExitCode != 0)
                throw new InvalidOperationException("Windows requires a restart to finish WSL installation. Restart, reopen Factory Workbench Setup and select Prepare SDK worker. Enrollment is retained.");
        }
        await new WorkbenchWorkerSetup(root, service).PrepareAsync(
            progress => status.Text = $"{progress.Stage}: {progress.Detail}", lifetime.Token);
        status.Text = "SDK worker prepared. Worker/SDK startup and full Linux shutdown passed under the service identity.\n" +
            "Repository and model credentials will be supplied by Factory for an authorized mission. No model call or workflow has started. " +
            "Next: configure the assigned production line in Factory, then select Start line.";
    }

    private async Task StartLineAsync()
    {
        var configuration = ConfigurationStore.Read(root);
        var binding = BindingStore.Read(root);
        using var certificate = MachineCertificates.Read(binding.CertificateThumbprint);
        using var machineHttp = PlantHttp.Create(configuration.PlantServiceUrl);
        var tokens = new CertificateTokenSource(configuration, binding, certificate);
        var plant = new PlantHttp(machineHttp, tokens.TokenAsync, tokens.RequestFreshToken);
        var current = await plant.ReadAsync<WorkbenchLineConfiguration>(
            $"v1/enrollments/{binding.EnrollmentId}/configuration", lifetime.Token);
        var line = current.ProductionLine ?? throw new InvalidOperationException("Assign this workbench to a plant with a production line in Factory first.");
        var execution = line.Definition.Execution ??
            throw new InvalidOperationException("Configure managed execution settings on this production line in the Factory website first.");
        if (!execution.Enabled)
            throw new InvalidOperationException("Enable this production line's execution settings in Factory first.");
        line.Definition.Validate();
        if (MessageBox.Show(this,
            $"Start continuous work on {line.Definition.Name}?\n\n" +
            $"Source: {line.Definition.Source.Repository}\nIssues: {line.Definition.Tasks.Repository}\n" +
            $"Model: {execution.Model}\nCommit author: {execution.GitAuthorName} <{execution.GitAuthorEmail}>\n" +
            $"Trusted reviewers: {string.Join(", ", execution.Reviewers)}\n\n" +
            "This permits repository instructions and project commands to run locally and publish draft PRs. " +
            "Factory delegates the line owner's connected repository and Copilot authority; no desktop login or tokens are copied.\n\n" +
            "The line selects eligible issues continuously, advancing only after its PR is merged and its worker has stopped. " +
            "Factory never merges automatically. A plant, line binding or repository change requires new local consent.",
            "Authorize and start line", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var started = DateTimeOffset.UtcNow;
        await new WorkbenchExecutionSetup(root, service).StartAsync(plant, binding, current, lifetime.Token);
        status.Text = "Local execution consent saved. Waiting for the service to report line readiness...";
        var observed = await WaitForConnectionAsync(started);
        status.Text = $"Local execution authorized. Workbench status: {observed.State}.\n{observed.Reason}\n" +
            "Factory supplies short-lived credentials for authorized missions. Use Stop line to revoke local execution permission.";
    }

    private async Task RetryMissionAsync()
    {
        var snapshot = WorkbenchMissionDiagnostics.ReadSnapshot(root);
        if (MessageBox.Show(this,
            $"Retry frozen mission {snapshot.Mission.Id} for {snapshot.Mission.Issue.Repository}#{snapshot.Mission.Issue.Number}?\n\n" +
            "The service must confirm prior worker termination. Retry is refused if candidate commits, publication effects, " +
            "changed Git ownership, authority or exhausted limits require reconciliation.\n\n" +
            "The old checkout and state are preserved. A fresh checkout uses the same frozen mission and remaining budget. " +
            "This explicitly permits another model attempt, verification and draft PR publication. Durable mission telemetry will record stage and error details.",
            "Explicit mission retry", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        status.Text = "Requesting explicit retained-mission retry; checking ownership and recording telemetry...";
        var result = await new WorkbenchMissionRetrySetup(root, service).RequestAsync(lifetime.Token);
        status.Text = result.Detail + "\nRetry accepted, not mission completion. Use View mission telemetry for the live trace, then Save diagnostics after the worker stops.";
    }

    private async Task<WorkbenchRuntimeStatus> WaitForConnectionAsync(DateTimeOffset started)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (File.Exists(LocalState.RuntimeStatusPath(root)))
            {
                var observed = LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(root));
                if (observed.ObservedAt >= started && observed.PlantConnected &&
                    service.RunningProcessId(LocalState.ReadInstallation(root)) == observed.ProcessId)
                    return observed;
                status.Text = "Waiting for service-account authentication: " + observed.Reason;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    private Task UnenrollAsync()
    {
        var product = DiagnosticControls.InstallerValue("ProductCode");
        if (!Guid.TryParse(product, out var id))
            throw new InvalidDataException("Use Installed Apps or the original MSI to uninstall this package.");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msiexec.exe")
        {
            Arguments = $"/x {{{id:D}}}",
            UseShellExecute = true
        }) ?? throw new IOException("Windows Installer could not start.");
        closing = true;
        return Task.CompletedTask;
    }

    private void AddAction(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => await Run(async () => { await action(); status.Text = text + " completed. Machine identity and journals retained."; });
        actions.Controls.Add(button);
    }

    private async Task Run(Func<Task> action, string stage = "setup")
    {
        if (busy)
            return;
        busy = true;
        signIn.Enabled = install.Enabled = unenroll.Enabled = actions.Enabled = false;
        try
        {
            diagnostics.Record(stage, "started");
            await action();
            diagnostics.Record(stage, "completed");
        }
        catch (OperationCanceledException)
        {
            var logged = diagnostics.TryRecordFailure(stage, "cancelled");
            status.Text = stage switch
            {
                "line-start" => "Line startup verification was interrupted. Local consent may already be saved; use Stop line to revoke it. Machine identity and journals are retained.",
                "mission-retry" => "Waiting for retry acknowledgement was interrupted. The request may still be queued or running; inspect mission telemetry. Do not submit another retry or delete its request. Stop line revokes execution consent.",
                _ => "Software installed; enrollment/connection verification incomplete or cancelled. Progress is retained. Resume setup; do not delete identity or journals."
            };
            if (!logged)
                status.Text += "\nThe diagnostic log could not be written. Check directory access and the MSI log.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ContractValidationException or MsalException or CryptographicException or
            InvalidOperationException or HttpRequestException or ArgumentException or System.ComponentModel.Win32Exception or AggregateException)
        {
            var logged = diagnostics.TryRecordFailure(stage, "failed", error);
            status.Text = "Setup/enrollment incomplete: " + error.Message + "\nSetup log: " + diagnostics.FilePath;
            if (!logged)
                status.Text += "\nThe diagnostic log could not be written. Check directory access and the MSI log.";
            MessageBox.Show(this, status.Text, "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            signIn.Enabled = configurationReady;
            install.Enabled = configurationReady && !removalPending && (File.Exists(Path.Combine(root, "binding.json")) ||
                enrollment is not null);
            unenroll.Enabled = DiagnosticControls.InstallerValue("ProductCode") is not null;
            actions.Enabled = configurationReady && !removalPending && File.Exists(Path.Combine(root, "installation.json"));
            if (closing)
                Close();
        }
    }
}
