using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Workbench.Contracts;
using Workbench.Worker;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--help"])
{
    Console.WriteLine("Workbench.Worker --workspace-root <Linux-path>\nReads a WorkerRequest JSON line from stdin, then optional {\"type\":\"cancel\"}.");
    return 0;
}

WorkerRequest? request = null;
var outputLock = new object();
void Emit(WorkerEvent item)
{
    lock (outputLock)
        Console.WriteLine(Wire.Write(item));
}

try
{
    if (!OperatingSystem.IsLinux())
        throw new PlatformNotSupportedException("The worker executes inside WSL/Linux.");
    var options = new Arguments(args, ["--workspace-root"], []);
    var workspaceRoot = options.Required("--workspace-root");
    if (!Path.IsPathFullyQualified(workspaceRoot))
        throw new ArgumentException("The worker workspace root must be an absolute Linux path.");
    var line = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("A worker request is required.");
    if (line.Length > 1024 * 1024)
        throw new InvalidDataException("Worker request exceeds 1 MiB.");
    request = Wire.Read<WorkerRequest>(line);
    request.Assignment.Validate();
    request.Methods.Validate();
    if (request.TimeoutSeconds is < 1 or > 86400)
        throw new InvalidDataException("Invalid worker timeout.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
    Console.CancelKeyPress += (_, item) => { item.Cancel = true; cancellation.Cancel(); };
    using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
        context => { context.Cancel = true; cancellation.Cancel(); });
    _ = Task.Run(async () =>
    {
        try
        {
            var signal = await Console.In.ReadLineAsync();
            if (signal is not null && JsonDocument.Parse(signal).RootElement.GetProperty("type").GetString() == "cancel")
                cancellation.Cancel();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("Invalid cancellation message: " + error.Message);
            cancellation.Cancel();
        }
    });
    var runner = new WorkerRunner(new CommandRunner(), new CopilotAgentRunner());
    var result = await runner.RunAsync(request, workspaceRoot, Emit, cancellation.Token);
    Emit(new("result", request.Assignment.Id, "complete", Result: result));
    return result.Status == "succeeded" ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    var id = request?.Assignment.Id ?? "unknown";
    Emit(new("result", id, "failed", Result: new()
    {
        AssignmentId = id,
        Status = "failed",
        Error = error.Message
    }));
    return 1;
}
