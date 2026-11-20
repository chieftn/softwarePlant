# Plant and Workbench REST Contract

[`openapi.yaml`](openapi.yaml) is the source contract for the first plant and workbench operations. It is OpenAPI 3.0.3, authored directly; no TypeSpec compiler or Node toolchain is required. The service now implements these endpoints with Entra bearer authentication and Cosmos persistence. Live configuration is described in the [service README](../README.md).

| Method | Route | Purpose |
|---|---|---|
| GET | `/v1/plants` | Paginated plants the caller can read. |
| GET | `/v1/plants/{plantId}` | Current plant representation and ETag. |
| POST | `/v1/plants` | Create a plant with the authenticated user as owner. |
| PUT | `/v1/plants/{plantId}` | Replace editable properties (currently name), using If-Match. Does not upsert. |

Creation and update accept `schemaVersion: "1"` and `name`. Owner, IDs, lifecycle state and timestamps are server-controlled. Creating a plant does not provision infrastructure. Ownership transfer, role management, lifecycle transitions, execution, and deletion are outside this increment.

The contract requires Entra bearer authentication, current plant authority, mutation idempotency and conditional updates. It defines JSON responses and Problem Details; browser cookies and HTML redirects do not authenticate the REST API. Future HTML/Datastar actions will use the same application operations under their own cookie/antiforgery boundary.

## Workbench Inventory and Reports

Use `/v1/plants/{plantId}` as `P` below.

| Method | Route | Purpose |
|---|---|---|
| GET | `P/workbenches` | Paginated inventory filtered by current plant/resource read authority. |
| GET | `P/workbenches/{workbenchId}` | Admission, approved identity, observed metadata and presence for one workbench. |
| PUT | `P/workbenches/{workbenchId}/registration` | Replace installed versions, machine name and capabilities reported by that active daemon. |
| PUT | `P/workbenches/{workbenchId}/presence` | Report local availability and record server-observed contact. |

Inventory uses delegated human authentication. Resource-scoped readers see only authorized workbenches, not their siblings. Registration and presence instead require an application `Workbench.Connect` token and the current active identity binding for the exact plant/workbench. Even owners and registrars cannot call those writes as the daemon.

Registration and presence return 204 after persistence. Both require `schemaVersion: "1"` and an `Idempotency-Key`; an identical retry does not refresh server timestamps. A fresh report needs a fresh key, and the daemon resolves outstanding reports before sending newer ones. Presence is nominally reported every 30 seconds and becomes stale after 90 seconds.

Admission, reported availability and contact freshness are separate. Reported capabilities are not permission grants. Registration does not enroll or activate a machine, and presence never claims work, heartbeats an assignment or frees an execution slot. Execution and operator controls remain outside this slice. No generic workbench-create endpoint is defined.

## Service-Wide Enrollment and Plant Assignment

Use `/v1/enrollments` as `E` below. These are service-wide, not plant-scoped routes.

| Method | Route | Purpose |
|---|---|---|
| GET | `E` | Enrollment-administrator inventory, including unassigned and retired devices. |
| POST | `E/challenge` | Enrollment-administrator challenge for an immutable machine draft and public certificate. |
| POST | `E` | Verify key possession and persist background provisioning; return 202/status URL, initially unassigned. |
| GET | `E/{operationId}` | Current state for any authorized enrollment administrator. |
| PUT | `E/{operationId}/assignment` | Assign/move/unassign using `expectedVersion`; omit `plantId` to unassign. |
| POST | `E/{operationId}/revoke` | Revoke access and retire owned directory identity; optional original draft cancels an unsubmitted operation. |
| POST | `E/{operationId}/retire-self` | Exact certificate-authenticated device identity retires only its own ready/active enrollment; idempotent retirement retries. Empty body; key equals enrollment ID. No new Graph/admin grant. |
| POST | `E/{operationId}/activate` | Match the provisioned application token before activating enrollment. |
| GET | `E/{operationId}/connection` | Machine reads its current nullable plant assignment and version. |
| PUT | `E/{operationId}/presence` | Active machine reports Factory presence, including while unassigned. |

Human enrollment operations require delegated `Plant.Access`, `Factory.User` and the explicitly assigned `Factory.EnrollmentAdministrator` role. Plant ownership alone is insufficient. Assignments additionally require owner/plant-registrar authority over source and destination plants. The website uses the same operations with cookie authentication and antiforgery protection. Devices enroll without selecting a plant; assignment is a separate website action.

Persist the original operation ID/key before submitting. Retrying an identical draft may use a fresh challenge; changing the draft under that ID returns 409. Provisioning checkpoints, service-wide installation reservations and ownership/public-key reconciliation prevent blind duplicate directory creation. `needsAttention` requires reconciliation, not a new identity. Activation and retirement require `Idempotency-Key` equal to the enrollment ID. Retirement retries return current status. MSI `0.1.4` can complete owned-local removal after a bounded retirement attempt, even if unconfirmed; only an acknowledged server transition proves revoked access. Accepted retirement must finish without the device/key/journal.

Assignment uses `expectedVersion` rather than `Idempotency-Key`. Moves/unassignments retain identity; plant registration/presence must carry the current `assignmentVersion`. Stale reports are rejected even after moving back to the same plant. An already-authorized in-flight plant request may finish; obsolete membership views suppress its reports. No workload execution/cancellation is implemented.

Retirement revokes access before deleting only the recorded, owned machine identity. It reconciles provisioning races and preserves audit history; `retirementNeedsAttention` keeps access revoked and requires an explicit retry after reconciliation. Unassign, retirement and software uninstall are different operations.

New enrollment is disabled without explicit server provisioning configuration. Disabling new enrollment does not disable existing machine connections, status or retirement; retain the provisioner configuration to process directory cleanup. The installer receives neither a client secret nor Graph grants. The resulting machine uses its own certificate for `Workbench.Connect`, never the human's token. See [bootstrap](../bootstrap/tenant/README.md#enable-workbench-enrollment) and [installer](../README.md#windows-installer-and-registration).

## Production-Line Configuration Synchronization

| Method | Route | Purpose |
|---|---|---|
| GET / POST | `/v1/production-lines` | List owned lines or create a configurable line owned by the current Factory user. |
| GET / PUT | `/v1/production-lines/{lineId}` | Read/update an owned definition; PUT requires an idempotency key and current strong ETag. |
| GET / PUT | `P/production-line` | Read the plant's assignment; assign/revoke under plant and line ownership using `expectedVersion`. |
| GET | `E/{operationId}/configuration` | Exact active machine obtains current membership, binding and desired line configuration, including while unassigned. |
| PUT | `E/{operationId}/configuration-observation` | Exact active machine durably acknowledges its configuration identity/state, using an idempotency key. |

Definitions and immutable configuration revisions live in Cosmos. Source repository/base branch/workload folder, independent issue selection, logical connection references and execution limits are configurable. This is the service registration contract, not a replacement for the existing production-method metadocument schema. Instructions remain in the referenced repository folder.

The first binding supports one configured line per plant; a line can be assigned to several owned plants. Assigning requires ownership of both line and plant, not an existing workbench. All active enrolled members inherit the plant's current line, including devices assigned later. Inheritance is configuration delivery, not execution readiness or a grant beyond local policy. No new directory role grants are required. Device admission/membership remains separate.

Bindings and their audit are committed in the plant partition using conditional writes. Configuration observations remain fenced by enrollment identity, membership version, plant-binding version and line revision. Device unassignment/move revokes its old configuration; joining or returning inherits the destination plant's current assignment under a new membership version. Clearing or replacing a plant's line revokes the old configuration for every member. The host discovers changes on its existing 15-second connection loop, clears local revoked configuration, and acknowledges through its Factory route. Network request timeouts add to discovery latency; a server-side change is not proof of immediate remote termination.

The initial workbench-scoped assignment API is removed rather than silently widened to change an entire plant. Existing workbench-partition bindings are retained but ignored. Explicitly assign the plant after deploying the correction; no legacy binding is automatically promoted. The configuration-discovery host increment has not been installed, so this corrected service is the prerequisite for its first rollout.

The current host persists the received configuration under `runtime\line-configuration.json` and reports `blocked` for a defined line, or `unconfigured` when there is none. It does not fetch the repo workload, install prerequisites, claim issues, invoke Copilot, or launch worker processes. `ready` reports are rejected. Existing connection-only clients remain non-executable but do not acknowledge configuration until upgraded. The new host requires the service's configuration endpoints before rollout.

Observations contain membership/binding/line revisions and server timestamps. New stale reports return `configuration_changed`; identical retries do not refresh or overwrite a newer observation. The website displays desired and observed configuration separately. No claim is released by an observation. Actual hard-stop controls for future SDK/worker execution, bounded contact-loss stopping, and graceful quiescing remain future work.

## Validation

Validate parsing, references and OpenAPI rules from the repository root:

```powershell
dotnet test .\src\Factory\Factory.Tests\Factory.Tests.csproj `
    --filter FullyQualifiedName~PlantApiContractTests
```

This checks the document, not implementation conformance. `PlantApiTests`, `WorkbenchApiTests`, and `StorageRecordTests` cover runtime behavior using signed test JWTs, an isolated transactional store, and the Cosmos record serializer. No test-only identity or store is selected by runtime configuration.
