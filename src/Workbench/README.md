# Workbench

A Windows .NET host runs a coding worker inside WSL. The worker uses Microsoft Agent Framework's Copilot integration. A work distributor delivers explicit assignments to named workbench daemons over outbound HTTP connections; GitHub Actions is not the dispatch mechanism.

## Layout

| Project | Responsibility |
|---|---|
| `Workbench.Contracts` | Assignment, selected production methods, progress/result messages, and process execution. |
| `Workbench.Distributor` | Container-hosted HTTP service; authenticates operators/workbenches and persists assignments, claims, heartbeats, and results in SQLite. |
| `Workbench.Host` | Windows entry point; launches the Linux worker through `wsl.exe`, streams events, and collects artifacts. Its daemon mode polls the distributor; submission mode sends assignments without launching a worker. |
| `Workbench.Worker` | Linux entry point; creates a checkout/branch, runs the MAF/Copilot agent, verifies changes, and optionally publishes a PR. |
| `Workbench.Tests` | Contract, orchestration, permission-decision, and failure-path checks without live LLM or GitHub calls. |

The pinned packages are `Microsoft.Agents.AI.GitHub.Copilot` 1.28.0 and `GitHub.Copilot.SDK` 1.0.31. The SDK's native permission-decision API carries an experimental diagnostic; suppression is limited to that callback.

## Build and Publish

On the build machine, install the .NET 10 SDK. From the repository root:

```powershell
dotnet test .\src\Workbench\Workbench.slnx
.\src\Workbench\publish.ps1
```

The package appears under `src\Workbench\artifacts`, with `win-x64\host`, `linux-x64\worker`, and `deploy.ps1`. Use a fresh `-OutputDirectory` for subsequent packages; publication rejects nonempty destinations to avoid mixing versions or runtime modes. The SDK acquires and verifies its pinned Linux Copilot runtime during worker publication. The output targets x64 Windows and x64 Linux; it is framework-dependent, not a single-file or self-contained installation.

Alternatively, `publish.ps1 -UseInstalledCopilot` omits runtime acquisition. In that case the WSL worker requires `COPILOT_CLI_PATH` pointing to an installed, compatible Copilot executable. This variable must be available to non-interactive WSL processes; setting it only in an interactive shell profile is insufficient.

## Devbox Prerequisites

- Windows x64 with WSL and a configured Linux x64 distribution.
- .NET 10 runtime on Windows and inside that distribution.
- `git` and GNU `timeout` in WSL. PR publication additionally needs `gh`.
- Copilot model access/authentication for the WSL user that runs the worker.
- Git repository authentication and author identity in WSL; PR publication also needs GitHub CLI authentication and repository push/PR permissions.
- Product-specific build/test tools installed in WSL.

Windows authentication and tools are not automatically inherited by the Linux worker. Provision credentials outside assignment files and production-line definitions. For GitHub CLI-backed HTTPS Git credentials, configure the GitHub CLI's Git integration in WSL.

## Deploy

Copy the published package to a devbox and run its deployment script:

```powershell
.\artifacts\deploy.ps1 -Distribution Ubuntu
```

When publishing and deploying directly from this repository, use `.\src\Workbench\artifacts\deploy.ps1` instead.

The script checks prerequisites, copies the host into `%LOCALAPPDATA%\Workbench`, and copies the worker into the WSL user's `.local/share/workbench/worker` directory. It prints the actual worker and workspace paths. It does not install dependencies, configure credentials, create a Windows service, or start an agent. Rerunning deployment replaces package files; automated upgrade/rollback management is not implemented.

## Run One Assignment

Use the [sample assignment](examples/assignment.json) as a starting point. Review its task, acceptance criteria, repository base branch, and verification commands before execution. An existing base branch with at least one commit is required; initialize an empty target repository separately.

From the repository root, using the paths printed by deployment:

```powershell
& "$env:LOCALAPPDATA\Workbench\Workbench.Host.exe" `
    --assignment .\src\Workbench\examples\assignment.json `
    --line .\examples\genui\production-line.json `
    --worker coder `
    --distro Ubuntu `
    --worker-path "/home/<user>/.local/share/workbench/worker/Workbench.Worker.dll" `
    --workspace-root "/home/<user>/.local/share/workbench/assignments" `
    --output "$env:LOCALAPPDATA\Workbench\results" `
    --allow-shell
```

This runs the agent, verifies its changes, and creates a local commit. Add `--publish-pr` to explicitly authorize pushing the branch and creating the PR. Without that flag, neither operation is performed by the worker host. `--model` optionally selects a model available to the authenticated runtime. `--timeout-seconds` defaults to 1800.

Use a distinct assignment ID on each devbox. The ID determines the workspace and `workbench/<id>` branch. Existing local assignment directories and remote branches are rejected rather than overwritten or automatically replayed.

## Distribute Assignments

See [distributor operation](Workbench.Distributor/README.md) for container configuration, authentication, API contracts, and interruption recovery.

On each devbox, provision `WORKBENCH_TOKEN` through the process environment. Its value must match that workbench's configured distributor token. Using the paths printed by deployment:

```powershell
& "$env:LOCALAPPDATA\Workbench\Workbench.Host.exe" `
    --daemon --distributor "https://<distributor-host>" --workbench box-one `
    --state "$env:LOCALAPPDATA\Workbench\daemon" `
    --repositories "chieftn/productionOutput1" `
    --distro Ubuntu `
    --worker-path "/home/<user>/.local/share/workbench/worker/Workbench.Worker.dll" `
    --workspace-root "/home/<user>/.local/share/workbench/assignments" `
    --output "$env:LOCALAPPDATA\Workbench\results" `
    --allow-shell --publish-pr
```

Each daemon runs one assignment at a time. Use a unique workbench ID/token and a persistent local state directory on each devbox. Do not copy daemon state between machines or run multiple instances under one identity. The repository allowlist and permission flags are local limits: a remote request cannot enable agent shell access or PR publication unless the daemon permits it. Assignment verification commands still execute as trusted operator-supplied code, independently of the agent shell flag.

On the submitting machine, provision `DISTRIBUTOR_ADMIN_TOKEN` and submit an assignment:

```powershell
& "$env:LOCALAPPDATA\Workbench\Workbench.Host.exe" `
    --submit --distributor "https://<distributor-host>" --workbench box-one `
    --assignment .\src\Workbench\examples\assignment.json `
    --line .\examples\genui\production-line.json `
    --allow-shell --publish-pr
```

Submission snapshots the selected production methods, model, timeout, and requested permissions. It returns an assignment-state receipt. It does not execute locally. `--worker`, `--model`, and `--timeout-seconds` work as in manual mode. Each additional assignment needs a new ID. HTTP is allowed only for a loopback distributor URL during local development; remote daemons require HTTPS.

Daemon mode is a foreground long-running process, not a Windows service installation. Ctrl+C requests cooperative cancellation. Use an operator-managed process supervisor for unattended startup; automatic service registration is not implemented.

The daemon persists claim intent before contacting the distributor, the claim before starting a worker, and the result before reporting completion. Network retries reuse the claim and result, never rerun the coding task. An interrupted assignment with no saved result stops daemon startup and requires reconciliation. Never delete its state to make it run again.

## Assignment and Methods

An assignment contains instructions, a GitHub `owner/repository` name, base branch, PR title, acceptance criteria, and verification commands. Commands are executable/argument arrays, not shell strings. If a shell script is needed, name the shell executable and script arguments explicitly.

The host reads the versioned production-line metadocument, selects a coder by ID, and resolves its skill/tool references. The worker receives a snapshot containing the line revision, business problem, role, guidance, and inline skill instructions. This adapter implements `workspace`, `verification`, and `source-control` capabilities; it rejects unsupported tool references instead of pretending they are available. It does not install skills or MCP servers.

Assignment files and production methods are trusted operator inputs. Verification commands are executable code. A selected skill's instructions are injected as guidance, not treated as a security policy.

## Execution and Results

The host sends a `WorkerRequest` as a UTF-8 JSON line over the WSL process's stdin. The worker emits JSON-line progress events and one terminal result on stdout; diagnostics use stderr. A later `{"type":"cancel"}` line requests cancellation.

The worker owns checkout, branch creation, commit, push, and PR operations. The coding agent is instructed to modify the checkout without performing those lifecycle operations. Host-run verification commands execute after the agent finishes. Agent text is not accepted as verification evidence or proof that a PR exists.

Publication pushes without force, creates a PR, and reads it back through `gh`, checking the repository URL, head branch/commit, base branch, and open state. No PR merge, review comment, or product release is performed.

Windows output contains `request.json`, `events.jsonl`, `worker-stderr.log`, and `result.json`. WSL retains the request, checkout, verification result, and PR-body artifact under the assignment directory. Command stdout/stderr are retained up to their final 64 KiB each, with a truncation flag in the result. Protect these artifacts as repository/task data; automatic retention management is not implemented.

Exit code zero means the configured execution completed and verification commands passed. It does not prove arbitrary natural-language acceptance criteria or authorize product release. When `--publish-pr` is requested, success also requires a matching PR.

## Permissions and Failure Boundaries

Read/write permission requests are limited by lexical checkout-path checks; Git metadata, sandbox-bypass requests, managed human-approval requests, and other permission kinds are denied. Workspace file hooks are disabled.

Shell execution is denied unless the operator supplies `--allow-shell`. Enabling it allows commands with the WSL user's permissions; this is **not** a command allowlist, filesystem sandbox, or guarantee that an agent cannot push through a shell. File-path checks also do not prevent symlink escapes. Use a dedicated devbox/WSL account with scoped credentials and appropriate isolation.

Cancellation and timeout request cooperative worker shutdown. Process execution stops owned process trees when cancelled. An outer GNU `timeout` provides a second Linux deadline with a ten-second forced-termination grace period. Killing `wsl.exe` alone is not treated as proof that every Linux descendant stopped; hard host failures can leave work running until that Linux deadline.

Interrupted work is not automatically resumed or retried. A push or PR creation may have succeeded before an error or cancellation was observed. Inspect local results and remote state before submitting a new assignment. The distributor does not add planning, review routing, MAF production workflows, remote cancellation, or product release.
