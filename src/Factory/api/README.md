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

## Device-Initiated Enrollment

| Method | Route | Purpose |
|---|---|---|
| POST | `P/enrollments/challenge` | Owner/plant-registrar challenge for an immutable machine draft and public certificate. |
| POST | `P/enrollments` | Verify key possession and persist authorized background provisioning; return 202/status URL. |
| GET | `P/enrollments/{operationId}` | Current state for the original enrolling principal with current plant authority. |
| POST | `P/enrollments/{operationId}/activate` | Match the actual provisioned application token before activating its pending admission. |

No separate approver or website enrollment mutation exists. Persist the original operation ID/key before submitting. Retrying an identical draft may use a fresh challenge; changing the draft under that ID returns 409. Provisioning checkpoints, per-installation reservations, ownership/public-key reconciliation, and conditional writes prevent blind duplicate directory creation. `needsAttention` requires administrator reconciliation, not a new identity.

Enrollment is disabled without explicit server provisioning configuration. The installer receives neither a client secret nor Graph grants; the background worker uses the administrator-configured provisioner. The resulting machine uses its own certificate for `Workbench.Connect`, never the human's token. Website inventory remains read-only. See [bootstrap](../bootstrap/tenant/README.md#enable-workbench-enrollment) and [installer](../README.md#windows-installer-and-registration).

## Validation

Validate parsing, references and OpenAPI rules from the repository root:

```powershell
dotnet test .\src\Factory\Factory.Tests\Factory.Tests.csproj `
    --filter FullyQualifiedName~PlantApiContractTests
```

This checks the document, not implementation conformance. `PlantApiTests`, `WorkbenchApiTests`, and `StorageRecordTests` cover runtime behavior using signed test JWTs, an isolated transactional store, and the Cosmos record serializer. No test-only identity or store is selected by runtime configuration.
