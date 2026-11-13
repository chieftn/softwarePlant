# Plant Coordinator

`Workbench.Distributor` hosts the plant's registration and distribution foundation in Microsoft Foundry Hosted Agents. It uses the official .NET AgentServer Invocations adapter and Foundry durable state store. The existing project name remains to preserve build paths.

```text
Windows workbench daemons
    | outbound HTTPS + Microsoft Entra authentication
Foundry Invocations endpoint
    | registration, assignment ownership, heartbeats, results
Foundry durable state store
```

The WSL MAF/Copilot workers still execute on the devboxes. The coordinator performs ordinary application operations without requiring a model call. MAF production planning, review workflows, automatic workbench selection, and product release remain outside this slice. No unused MAF workflow engine or second service is introduced.

## Foundry Hosting

Build from `src\Workbench`:

```powershell
docker build --platform linux/amd64 -f .\Workbench.Distributor\Dockerfile -t plant-coordinator .
```

The image defaults to Foundry mode, serves HTTP on the platform's `PORT` (8088 by default), exposes `GET /readiness`, and implements `POST /invocations`. The official host handles request context, diagnostics, and graceful shutdown. Mode selection fails closed: Foundry mode requires the platform environment; local mode is refused inside Foundry.

Use [foundry-agent.example.json](foundry-agent.example.json) as the create-agent request body after replacing its placeholders. It declares Invocations container protocol **2.0.0**, not a conversational Responses endpoint. Follow [Microsoft's deployment procedure](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) to push an immutable image tag, grant the project identity registry-pull access, create the agent, and wait for an active version. This repository does not provision Azure resources.

Configure these non-secret values per agent version:

| Variable | Meaning |
|---|---|
| `Coordinator__Mode` | `Foundry` in Azure. |
| `Coordinator__StoreName` | Stable, plant-specific name, for example `plant-main`. Keep the same agent and store name across versions. |
| `Coordinator__Operators__0` | Platform caller ID authorized to submit, inspect, and resolve work. Add indexed entries for other operators. |
| `Coordinator__Workbenches__box-one` | Platform caller ID pre-authorized to register and operate `box-one`. |
| `Coordinator__Workbenches__box-two` | A separate platform caller ID for `box-two`. |

These are the opaque caller IDs delivered by Foundry in `x-agent-user-id`; do not assume they equal an application client ID or a directory object ID. `{"operation":"identity"}` returns the authenticated caller ID. For initial discovery, deploy with no operator/workbench mappings: authenticated callers can identify themselves but cannot access plant operations. Then create a version with the approved mappings. Workbench mappings without an operator are rejected.

Foundry authenticates callers using Entra. Grant consumers appropriate endpoint access, such as Foundry Agent Consumer, and separately configure the application's allowlists above. Use distinct application identities for unattended devbox daemons; authenticating both under the same developer account does not identify two machines. The coordinator trusts only Foundry-generated caller/call headers, not `x-client-*` identity assertions or IDs in the request body. Do not expose the container directly outside Foundry: the platform gateway is the authentication boundary.

The running coordinator uses its hosted agent identity via Azure Identity to access Foundry storage. The project identity used for image pulls is different. No operator tokens, Git credentials, or Copilot credentials belong in the hosted image. Caller token refresh on Windows uses Azure Identity; provision credentials outside task files, for example a dedicated application with certificate authentication.

## Registration and Invocation Contract

The [Windows host](../README.md#distribute-assignments) uses `--foundry` with the full callable Invocations URL and a stable `--foundry-session` ID. Create/reuse a hosted session using Foundry's session tooling and keep its ID in daemon configuration. Do not create a new sandbox for every poll. Separate callers can use separate sessions; all authorized sessions share the plant store. Frequent polling keeps session compute active and incurs hosting charges.

On startup a daemon registers its machine name and supported capabilities under its pre-authorized identity. Registration persists the identity binding and updates metadata on reconnect; another identity cannot take over an existing record. Changing a registered identity requires an explicit future rebind procedure, not just changing configuration. Reported capabilities describe the host; they grant no permissions and are not installed-tool attestation. Local repository and execution limits remain authoritative.

Every request to `/invocations` contains a `CoordinatorInvocation`, for example:

```json
{
  "schemaVersion": "0.1",
  "operation": "get",
  "assignmentId": "example-task"
}
```

| Operation | Caller | Additional fields |
|---|---|---|
| `identity` | Authenticated endpoint consumer | None; returns the platform caller ID. |
| `register` | Owning workbench | `workbenchId`, `registration` with `machineName` and `capabilities`. |
| `workbenches` | Operator | None; returns registration, last seen, presence, and active assignment. |
| `submit` | Operator | `dispatch` containing `workbenchId` and a complete `WorkerRequest` under `request`. Target must already be registered. |
| `get` | Operator or owning workbench | `assignmentId`. |
| `claim` | Owning workbench | `workbenchId`, persisted `claimId` in GUID N format. |
| `heartbeat` | Owning workbench | `workbenchId`, `assignmentId`, `claimId`. |
| `result` | Owning workbench | The same identifiers plus terminal `result`. |
| `resolve` | Operator | `assignmentId`, reconciled terminal `result`. |

Replies use `CoordinatorReply` with the applicable `assignment`, `claim`, `workbench`, `workbenches`, or `principalId`. An empty claim reply means no queued work. Rejected operations use HTTP 400, 403, 404, or 409; storage failures use 503. Requests are limited to 1 MiB. This replaces the earlier standalone REST routes; update the coordinator and hosts together.

Presence is refreshed on registration, polling, assignment heartbeats, and completion. After 90 seconds without contact, a workbench reads as `offline`; an unresolved assignment without heartbeats reads as `attention`. Neither condition releases a claim.

## Persistence and Recovery

The store is **shared across callers** (`userIsolation: false`) because operators and machines must coordinate on the same records. The application enforces access. It sets item expiration to **never** and rejects an existing store with conflicting isolation/expiration settings rather than silently accepting unsafe retention.

A compact plant index contains registrations and assignment ownership. Each mutation uses create-only writes or an exact ETag precondition. Competing instances reload and retry only the state transition, never a coding run or PR operation. A single conditional update protects the one-active-assignment-per-workbench rule without requiring a cross-record transaction.

Assignment requests and results are separate immutable items written before the index references them. Interrupted writes can leave unreferenced items; no automatic deletion is performed. Retried operations verify the original payload. A failed submission before its index commit can reserve its request ID; retry with the same payload. Once indexed, reusing an assignment ID returns 409. A lost submission acknowledgement requires inspecting that ID first.

Each payload and the plant index has a 900 KiB safety limit below Foundry's 1 MB item limit. Large evidence belongs in external artifact storage; automatic artifact offload and index archival are not implemented. Oversized writes fail explicitly without completing or releasing the assignment. The initial single-index consistency design is for a small plant, not a high-throughput scheduler.

Claims are never automatically expired or reassigned. Repeating the same persisted claim ID returns the same active assignment. A different claim while work is unresolved, or reuse of a completed claim ID, returns 409. Terminal completion retries are idempotent; a different result cannot overwrite the first.

Daemon journals and WSL artifacts remain essential. A pending claim with no complete saved result stops daemon startup rather than rerunning coding work. Inspect WSL processes, the checkout, branch, and remote PR before resolving it. Kill or wait for remaining execution before freeing its slot. A host failure does not prove that Linux descendants stopped.

To resolve an inspected interruption, invoke:

```json
{
  "operation": "resolve",
  "assignmentId": "example-task",
  "result": {
    "assignmentId": "example-task",
    "status": "failed",
    "error": "Operator confirmed execution stopped and inspected branch/PR state. No replay is authorized."
  }
}
```

Then restart the daemon. A saved terminal result is reported without invoking the worker; a remotely resolved assignment clears the pending local journal. Success for a publication request requires an inspected PR URL. Operator resolution is an explicit assertion, not independent verification.

## Local Development

Local mode uses the **same Invocations adapter and SDK state-store API**, with the SDK's file-backed implementation. It is loopback-only, uses distinct local bearer tokens, and rejects simulated platform identity headers. It does not validate real Foundry identity propagation or hosted durability.

Set `Coordinator__Mode=Local`, `Coordinator__StoreName=plant-local`, and `AGENTSERVER_STATE_ROOT` to a dedicated persistent local directory. Provision `Coordinator__Local__OperatorToken` and `Coordinator__Local__Workbenches__box-one` through the process environment with distinct random values of at least 32 characters. Then, from `src\Workbench`:

```powershell
dotnet run --project .\Workbench.Distributor -- --urls http://127.0.0.1:8088
```

Run the daemon without `--foundry`, using `--distributor http://127.0.0.1:8088` and its matching `WORKBENCH_TOKEN`. Local operator commands use `DISTRIBUTOR_ADMIN_TOKEN`. Local credentials are rejected for remote URLs. The SDK's local files are not a production substitute for the Foundry service.

SQLite persistence and the old `Distributor__*` configuration are removed. Existing SQLite databases are not migrated automatically. Inspect and finish/reconcile previous work before switching; retain old artifacts and use new assignment IDs in the new plant store.

The official AgentServer dependencies are prerelease: Invocations `1.0.0-beta.8` and Core `1.0.0-beta.30`. Container publication and local tests do not establish tenant availability, actual platform authentication, or deployed recovery; those still require the two-devbox Azure proof.
