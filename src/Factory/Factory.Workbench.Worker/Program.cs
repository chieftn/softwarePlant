using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
namespace Factory.Workbench.Worker;

internal static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--help"] or [])
        {
            Console.WriteLine("Factory Linux worker: run --workspace-root <path> --instance <uuid> --nonce <hex> [--resume].\nRequires an explicitly enabled mission/credential packet over owned stdin; no interactive auth.");
            return 0;
        }
        if (args is ["git-credential", var credentialOperation])
        {
            if (credentialOperation != "get")
                return 0;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string? field;
            while (!string.IsNullOrEmpty(field = await Console.In.ReadLineAsync()))
            {
                var separator = field.IndexOf('=');
                if (separator > 0)
                    fields[field[..separator]] = field[(separator + 1)..];
            }
            var repository = Environment.GetEnvironmentVariable("FACTORY_GITHUB_REPOSITORY");
            var token = Environment.GetEnvironmentVariable("FACTORY_GITHUB_TOKEN");
            if (fields.GetValueOrDefault("protocol") != "https" || fields.GetValueOrDefault("host") != "github.com" ||
                fields.GetValueOrDefault("path") != repository + ".git" || string.IsNullOrWhiteSpace(token))
                return 1;
            Console.WriteLine("username=x-access-token\npassword=" + token + "\n");
            return 0;
        }

        var outputLock = new object();
        void Emit(WorkerPipeMessage message)
        {
            lock (outputLock)
                Console.WriteLine(JsonSerializer.Serialize(message, RepositoryWorkload.Json));
        }

        try
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Factory coding execution requires the service-owned Linux worker.");
            if (args is ["stop", "--workspace-root", var stopRoot, "--mission", var stopMission, "--instance", var stopInstance, "--nonce", var stopNonce])
            {
                var confirmed = await OwnedLinuxProcess.StopAsync(stopRoot, stopMission, stopInstance, stopNonce, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new { confirmed }));
                return confirmed ? 0 : 1;
            }
            if (args.Length is not (7 or 8) || args[0] != "run" || args[1] != "--workspace-root" ||
                args[3] != "--instance" || args[5] != "--nonce" || args.Length == 8 && args[7] != "--resume" ||
                !Guid.TryParseExact(args[4], "D", out _) || args[6].Length != 64 || !args[6].All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid owned worker invocation.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));
            if (!root.StartsWith('/') || root == "/" || !MissionPermissionPolicy.Within(root, root))
                throw new InvalidDataException("Worker requires a dedicated absolute non-root workspace directory.");
            var line = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("An enabled worker request is required.");
            if (Encoding.UTF8.GetByteCount(line) > 128 * 1024)
                throw new InvalidDataException("Worker request exceeds its transport bound.");
            var request = JsonSerializer.Deserialize<WorkerMissionRequest>(line, RepositoryWorkload.Json)
                ?? throw new InvalidDataException("Worker request is empty.");
            request.Validate();
            var run = Path.Combine(root, request.Mission.Id);
            var statePath = Path.Combine(run, "mission-state.json");
            var resume = args.Length == 8;
            if (Directory.Exists(run) != resume)
                throw new IOException("Existing execution state requires explicit reconciliation/resume; new work cannot overwrite it.");
            if (!MissionPermissionPolicy.Within(root, run))
                throw new IOException("Worker state directories cannot traverse filesystem links.");
            Directory.CreateDirectory(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var runLock = LocalState.Lock(run, "worker.lock");
            var ownedProcess = OwnedLinuxProcess.Register(run, request.Mission.Id, args[4], args[6]);
            Emit(new("started", args[4], "owned-process", JsonSerializer.SerializeToElement(ownedProcess, RepositoryWorkload.Json)));
            using var lifetime = new CancellationTokenSource();
            using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); });
            var plant = new WorkerPlantPipe(Emit);
            var reading = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } response)
                    {
                        if (Encoding.UTF8.GetByteCount(response) > 128 * 1024)
                            throw new InvalidDataException("Plant pipe response exceeds its transport bound.");
                        plant.Receive(JsonSerializer.Deserialize<WorkerPipeMessage>(response, RepositoryWorkload.Json)
                            ?? throw new InvalidDataException("Invalid plant pipe response."));
                    }
                }
                finally
                {
                    plant.Disconnect();
                    await lifetime.CancelAsync();
                }
            });
            var repository = new GitMissionRepository(request.Mission, run, request.GitAuthorName, request.GitAuthorEmail, plant, new());
            var agent = new CopilotMissionAgent(repository.Checkout, Path.Combine(run, "sessions"), request.Model, request.CopilotToken, request.Enabled);
            var runner = new DevelopmentMissionRunner(repository, agent, async (current, cancellationToken) =>
            {
                LocalState.Write(statePath, current, 512 * 1024);
                await plant.ReportAsync(new(current.Mission.Id, current.Mission.Digest, current.State, current.Iterations,
                    current.ActiveMilliseconds, current.CandidateCommit, current.PullRequest, current.PublicationPending, current.Reason), cancellationToken);
            }, plant.AuthorizeAsync, request.Reviewers, TimeProvider.System);
            var state = resume ? LocalState.Read<DevelopmentMissionState>(statePath, 512 * 1024) : DevelopmentMissionState.Claimed(request.Mission);
            if (state.Mission.Digest != request.Mission.Digest)
                throw new InvalidDataException("Resume request cannot replace frozen mission inputs.");
            if (resume && state.PublicationPending && state.State is "publishing" or "needsAttention")
                state = await runner.ReconcilePublicationAsync(state, lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                state = await runner.AdvanceAsync(state, lifetime.Token);
                if (state.State != "awaitingApproval")
                    break;
                await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            }
            Emit(new("result", request.Mission.Id, state.State, JsonSerializer.SerializeToElement(new { state.State, state.Reason })));
            return state.State == "approved" ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Worker stopped: {error.GetType().Name}. Reconcile owned execution and publication before retrying.");
            return 1;
        }
    }
}

