# Work Distributor

The distributor accepts explicit assignments addressed to configured workbenches. Windows hosts poll it outbound and execute through the existing WSL MAF/Copilot worker. The distributor contains no LLM, planner, reviewer, or production workflow engine.

## Host

Build from the `src\Workbench` directory:

```powershell
docker build -f .\Workbench.Distributor\Dockerfile -t workbench-distributor .
```

Configure these environment variables through your deployment's secret/configuration mechanism:

| Variable | Meaning |
|---|---|
| `Distributor__AdminToken` | Operator credential for submission, inspection, and explicit recovery. |
| `Distributor__Workbenches__box-one` | Unique daemon credential for `box-one`. |
| `Distributor__Workbenches__box-two` | Unique daemon credential for `box-two`. |
| `Distributor__Database` | SQLite file; defaults to `/data/distributor.db` in the container. |

Use distinct cryptographically random tokens of at least 32 characters. Do not commit credentials or put them in assignment files, images, or command-line arguments. The daemon receives only its own token, not the operator token.

For local development, set the configuration environment variables in the current shell, then:

```powershell
docker volume create workbench-distributor-data
docker run --rm --name workbench-distributor `
    -p 127.0.0.1:8080:8080 `
    --mount source=workbench-distributor-data,target=/data `
    --env Distributor__AdminToken `
    --env Distributor__Workbenches__box-one `
    --env Distributor__Workbenches__box-two `
    workbench-distributor
```

The image runs as the .NET non-root application user and prepares a writable `/data` directory. A bind-mounted directory must grant that user write access. Keep the database and WAL files together on persistent local storage. Do not use a network share or multiple service replicas against this SQLite file. The named volume preserves state when the container is replaced.

For two remote devboxes, deploy one service instance behind an authenticated-network boundary and HTTPS ingress, for example on an Azure VM/container host with persistent local disk. Check the hosting service's filesystem guarantees before choosing its volume: this SQLite implementation requires local filesystem locking and is not a scale-out database. The application authenticates API requests with bearer tokens; TLS termination, secret storage/rotation, database backups, and network restrictions belong to deployment. A token/configuration change requires a service restart.

You can run without Docker using .NET 10:

```powershell
dotnet run --project .\Workbench.Distributor -- --urls http://127.0.0.1:8080
```

Set the same environment variables first. Outside the container, the default database path is `data/distributor.db`, relative to the process working directory. `GET /healthz` is an unauthenticated liveness endpoint; it is not a workbench readiness signal.

This slice implements container hosting, not a Foundry Hosted Agents adapter. Foundry requires its hosting protocol and lifecycle integration; do not assume this HTTP service can be deployed there unchanged. The distribution contracts do not depend on MAF or Foundry.

## Protocol

All endpoints other than `/healthz` require `Authorization: Bearer <token>`. JSON uses the shared `Workbench.Contracts` types. The maximum HTTP request body is 8 MiB.

| Endpoint | Credential | Behavior |
|---|---|---|
| `POST /assignments` | Operator | Accept a `DispatchRequest` containing `workbenchId` and a complete `WorkerRequest` under `request`; return 201 with state. |
| `GET /assignments/{id}` | Operator or owning workbench | Read state, last heartbeat, and terminal result. |
| `POST /workbenches/{id}/claim` | That workbench | Send `{"claimId":"<GUID in N format>"}`; return a `WorkClaim`, or 204 when no work is queued. |
| `POST /workbenches/{id}/assignments/{assignment}/{claim}/heartbeat` | That workbench | Refresh the active claim; return 204. |
| `POST /workbenches/{id}/assignments/{assignment}/{claim}/result` | That workbench | Send a `WorkerResult`; identical completion retries return 204. |
| `POST /assignments/{id}/resolve` | Operator | Record a reconciled `WorkerResult` for an already claimed assignment. |

The [host submission command](../README.md#distribute-assignments) builds a snapshot using the existing production-line loader. Future production workflows can submit that same contract directly.

Workbench IDs are lowercase letters, digits, and hyphens, up to 63 characters. IDs and credentials are preconfigured; dynamic registration, capability discovery, and automatic workbench selection are not implemented. Assignment IDs are globally unique in the distributor. Reusing an ID returns 409 even when its previous run finished. A submission connection failure can occur after acceptance: inspect that ID before deciding what to submit next.

A workbench can hold one unresolved claim. Additional assignments wait in submission order. A retried claim with the same persisted claim ID returns the same assignment; a different claim ID while work is unresolved returns 409. This covers a lost claim response without issuing a second task.

## Recovery

States are `queued`, `running`, `attention`, `succeeded`, `failed`, and `cancelled`. Heartbeats run every 20 seconds while executing. After 90 seconds without one, an unresolved assignment reads as `attention`. This is a warning, not a claim expiry. Work is never automatically returned to the queue or transferred to another workbench.

The daemon keeps a local journal under its `--state` directory and artifacts under `--output`. Keep both persistent. A process-local file lock prevents concurrent daemons sharing the same state directory.

On restart:

- A saved claim intent retries that same claim request.
- A saved terminal result is reported again without invoking the worker.
- An assignment already completed or resolved at the distributor clears the pending local journal.
- A pending claim without a complete result stops startup. It does not run the assignment again.

For interrupted work, inspect the Windows artifacts, WSL processes/workspace, Git branch, and remote PR. Stop or wait for any remaining owned execution before releasing its slot. Killing the Windows host does not prove that Linux descendants stopped; the worker's Linux timeout remains the outer bound.

If a complete `result.json` exists at the host's expected output path, restart the daemon to report it. Otherwise, use the operator credential to post a reconciled terminal result to `/assignments/{id}/resolve`, then restart the daemon. A failed/cancelled result must explain what happened in `error`; a successful result for a publication request must include the inspected PR URL. For example, after inspection:

```json
{
  "assignmentId": "example-task",
  "status": "failed",
  "error": "Operator confirmed execution stopped; inspected the branch and PR state. No automatic replay is authorized."
}
```

Do not resolve an assignment merely to clear the queue. An operator result is an explicit assertion, not independent verification. Existing terminal results cannot be overwritten. If correction requires more coding, submit a separately identified assignment after inspecting existing effects.

Results and database contents can include repository code and task evidence. Restrict access, provision retention and backups, and keep model/Git credentials on the workbenches. Verification commands are executable operator inputs: possession of the operator token authorizes code execution within the daemon's configured environment. Repository allowlisting is not an OS sandbox.
