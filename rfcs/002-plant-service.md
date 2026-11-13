# RFC 002: Plant Service

Status: Draft proposal. Resource choices and API behavior below are proposed requirements, not deployed capabilities.

Companion: [RFC 001: Workbench Workloads and Installation](001-workbench-workloads-and-installation.md), which specifies machine credentials and enrollment.

Project structure: [RFC 003: Factory Solution and Projects](003-factory-solution-and-projects.md).

## Purpose

The Factory plant service hosts multiple plant instances and provides a web application and machine-facing HTTPS API for plant bookkeeping: ownership and access assignments, enrolled workbenches, line bindings, assignment queues, execution claims, presence, and results. Store these as independently updateable documents, not one whole-plant document.

A production-line coordinator may use MAF to decide what work to submit. The plant service validates and records that work without invoking a model. Workbenches and the plant view use the same service rules.

```text
MAF line coordinator ----\
Operator / plant view ---+--> Plant service --> Cosmos DB
Workbench host ----------/         |
                                  +--> Blob Storage: snapshots and evidence
                                  +--> Azure Monitor: operational telemetry
```

No client receives direct database credentials. Registration, polling, and heartbeat requests share application instances rather than requiring per-device hosted compute.

## Responsibilities and Limits

The plant owns device admission, identity bindings, capabilities, capacity, durable delivery, ownership, execution records, and audit evidence. A line owns its repositories, worker roles, workloads, methods, and verification requirements.

Store versioned line definitions and approved bindings as configuration. The service resolves them into assignment snapshots, but does not invent the workflow or make model-driven routing decisions.

Initially, each assignment targets one workbench and each workbench runs at most one assignment across all lines. Claims select the oldest eligible queued assignment, breaking ties by assignment ID. There is no shared unassigned queue, automatic machine selection, or automatic failover of running work.

Do not introduce a broker, microservice per concept, or dynamic plug-in loader for this slice. Database-backed queues are sufficient if the correctness and capacity tests below pass.

## Plant Instances and Scoped Access

Each plant has an ID, name, owning Entra tenant/user object ID, lifecycle state, and role assignments. The service is single-tenant initially; multiple plants do not imply cross-tenant directory provisioning.

```text
Plant service
    Plant A: owner, scoped role assignments
        Workbenches
        Other resource types as supported
    Plant B: owner, scoped role assignments
        Workbenches
        Other resource types as supported
```

An access assignment records the principal's tenant/object ID, role, and scope. Names and email addresses are display metadata, not identity keys. Initially support individual users and application principals; group membership requires an explicit resolution policy before support is added.

Service-level `Factory.PlantCreator` authority permits creation. Create the plant, owner binding, and audit event in one transaction. Its owner can delegate plant roles and transfer ownership through an audited, conditional update; do not allow removal of the owner without a valid replacement.

| Plant role | Authority |
|---|---|
| Owner | Manage the plant, its scoped access assignments, resource admission, and owner transfer. |
| `Workbench.Registrar` | Register, approve, recover, and revoke workbench identities in this plant; cannot assign roles or administer Entra directly. |
| `Plant.EnrollmentRequester` | Submit enrollment requests and inspect their own requests; cannot approve them. |
| `Plant.Operator` | Operate approved resources and reconcile execution within scope; line/workload grants remain separate. |
| `Plant.Reader` | Read authorized plant/resource state without mutation. |

Plant-scoped permissions apply to its child resources where the role permits the action. Resource-scoped assignments can grant narrower access without authority over sibling resources or the plant. Registration of a new workbench requires authority on its parent plant. Adding a resource type does not silently extend the workbench registrar's permissions.

Plant ownership is application authority, not Azure subscription ownership or an Entra administrator role. Owning Plant A gives no access to Plant B. Production lines bind to resources through explicit grants; they are not owned by this resource hierarchy, and plant access does not bypass local execution limits.

## Azure Deployment

Use a single ASP.NET Core/.NET 10 deployment on Linux Azure App Service for the HTML web application, machine API, and background enrollment processing. These are separate interfaces/responsibilities, not separate deployments by default. Publish a self-contained Linux build so runtime installation does not depend on the host image's .NET version.

| Resource | Configuration and purpose |
|---|---|
| App Service plan and web app | Standard or higher plan supporting Always On, deployment slots, VNet integration, and autoscale. One development instance; two minimum production instances. A provisional production maximum of four is subject to load/cost testing. |
| Azure Cosmos DB for NoSQL | One write region, strong consistency, provisioned autoscale throughput. Database `plant`, container `records`, partition key `/scopeId`. Set the RU ceiling from measured workload and budget. |
| Azure Blob Storage | Private containers for immutable assignment snapshots, evidence, and archives. No anonymous access or account keys in application configuration. |
| User-assigned managed identities | Explicit service data-access and privileged directory-provisioning identities. Staging uses separate identities, database, and directory test applications. |
| Microsoft Entra ID | Web application/API and installer registrations, service-entry permissions, daemon application roles, and administrator-consented background Graph access. Plant roles remain database records. |
| Application Insights and Log Analytics | Request traces, failures, workload-stage events, storage RU/latency, metrics, and alerts. |
| Azure Key Vault | Web application's confidential-client credential, cookie key-ring protection key, and server integration secrets if required. It does not hold workbench private keys or personal GitHub tokens. |
| VNet, private endpoints, private DNS | App Service outbound integration to Cosmos, Blob, and Key Vault. Disable their public data access. |

The plant API remains publicly reachable over HTTPS because workbenches connect outbound from existing networks. Require TLS, token validation, request-size limits, and rate limits. Expose only minimal public discovery and liveness responses; protect operational details.

Initial deployment is single-region, not a regional disaster-recovery promise. Enable Cosmos continuous backup and Blob recovery protections, set explicit retention, and exercise a restore into an isolated environment. Never resume restored claims until old workbench execution and external effects are reconciled.

Provision Azure resources, networking, data-plane roles, diagnostics, alerts, and deploy settings through checked-in Bicep and repeatable scripts. A privileged bootstrap script provisions Entra registrations, initial service-entry grants, and background Graph permissions once per deployment, not once per workbench. Do not assume an Azure resource-group deployment grants directory administration.

The service's data-access identity receives:

- Cosmos DB Built-in Data Contributor scoped to the plant database.
- Storage Blob Data Contributor scoped to the required containers.
- Key Vault Secrets User scoped to the web sign-in credential and required integration secrets.
- Key Vault Crypto User scoped to the cookie key-ring protection key.
- Monitoring Metrics Publisher scoped to Application Insights, using Entra-authenticated ingestion.

The data-access identity has no Graph write permissions. A separate, explicitly selected provisioning identity receives administrator-consented Graph application permissions `Application.ReadWrite.OwnedBy`, `Application.Read.All`, and `AppRoleAssignment.ReadWrite.All`. Create workbench directory objects with provisioner ownership and restrict lifecycle operations to tracked objects. Azure deployment automation uses its own federated identity and scoped management permissions.

`AppRoleAssignment.ReadWrite.All` is broad tenant authority, not restricted by plant RBAC or the owned-application permission. The provisioner only assigns the configured `Workbench.Connect` role on the configured API, never caller-selected roles/resources. These application checks do not narrow the underlying Graph grant. Both identities in one App Service share a compromise boundary; separate identities aid credential selection and audit, not process isolation. Production enablement requires explicit tenant-administrator acceptance of this authority, or isolation of provisioning into a separately protected worker if required by tenant policy.

## HTML-First Web Application

Use server-rendered HTML with a pinned, self-hosted Datastar library for reactive signals and HTML updates. Do not require a SPA framework, a browser-side copy of the domain model, or a full client-side router. Select the server template renderer during implementation; it must support safe HTML/attribute escaping and reusable fragments.

The first page lists plants the signed-in user can access, not all plants. A plant page shows its owner, effective role assignments, resources, enrollment requests, and execution state. Provide owner/role management and workbench admission through the same application operations used by the API.

Use signals for local interaction state such as filters, selections, and pending actions. The server authorizes every action, commits the operation, and returns HTML and/or signal patches. A browser patch is not evidence that a background operation completed.

Use ordinary HTML responses for initial pages and short interactions; use Datastar SSE where live enrollment progress or execution status is needed. Authorize the subscription and recheck current access before emitting updates. Reconnect by reading authoritative current state; do not depend on an in-memory notification in one API instance. No separate event broker is required for the initial bounded status refresh.

Keep IDs stable for DOM updates, preserve focus and form state, and expose progress/errors accessibly. Signals, route IDs, and submitted forms are untrusted input. Limit subscription rates and lifetime, close streams on loss of access/session expiry, and verify proxy flushing and reconnect behavior on App Service.

## Identity and Authorization

Use Entra OpenID Connect authorization-code sign-in with PKCE for the web application and a secure, HTTP-only session cookie. Use a confidential-client credential held server-side, antiforgery protection on mutations including Datastar requests, and explicit session expiry/revalidation. Do not put API access tokens in browser storage.

Persist ASP.NET Core Data Protection keys in a private Blob container and protect them with the Key Vault key. Instances share the application/key-ring configuration so cookies survive scale-out and restart. Keep staging keys and cookies separate.

The JSON API accepts bearer tokens, not web-session cookies or HTML login redirects. The installer uses a public client with delegated `Plant.Access`; daemons and line coordinators use application tokens. Validate the configured tenant issuer, audience, signature, lifetime, and required scope/role. Configure version-2 access tokens; the accepted API audience is its application client ID. Clients request scopes under `api://<client-id>`.

Do not trust caller-supplied identity headers. Derive identity from the validated token's tenant and object ID. Store client ID separately; it is not interchangeable with service-principal object ID.

| Caller | Authentication / service-entry permission | Additional service restriction |
|---|---|---|
| Web user | Entra sign-in and `Factory.User` service-entry role | Owner or scoped database role determines plant/resource actions. |
| Installer user | Delegated `Plant.Access` scope and `Factory.User` role | Owner/registrar may authorize enrollment; request-only role requires separate approval. |
| Plant creator | Human authentication plus `Factory.PlantCreator` role | May create a plant and become its initial owner; no access to unrelated plants. |
| Line operator | Human authentication plus explicit line grant | Submit/read/cancel work only within authorized lines and plant resource bindings. |
| MAF line coordinator | Application `Line.Coordinator` role | Bind its tenant/object ID to specific lines and allowed workbenches. |
| Workbench | Application `Workbench.Connect` role | Require an active tenant/object-ID binding; access only its own assignments and bounded execution evidence. |

Entra service-entry roles and application roles have their corresponding allowed member types. The application service principal requires assignment. Service entry is not plant authority: ownership and scoped roles are stored and evaluated by this application, not encoded as a tenant-wide `Plant.Owner` role.

Both HTML and JSON interfaces call the same application operations and plant authorization rules without loopback HTTP or duplicated business logic. Keep browser requests same-origin. The installer has no embedded secret or Graph permissions; the background provisioner uses its own identity, not an on-behalf-of user token.

An Azure-hosted line coordinator should use managed identity assigned the API application role and a persistent line grant. Non-Azure automation requires an explicitly configured workload identity or certificate, not an operator's interactive token.

Evaluate current admission and scoped grants on each mutation; do not rely solely on token expiry or cache revoked approvals. A revocation denies subsequent authorized operations, but cannot undo an operation already in flight. Enforce workbench admission again in its claim transaction.

## Background Enrollment and Credential Provisioning

Enrollment follows RFC 001. Return `202 Accepted` only after committing the request, its approval decision, and durable operation state. An owner/registrar's explicit submission can approve immediately; request-only users need an authorized approver. Record requester and approver identities independently from the resulting daemon identity.

Persist `pending`, `approved`, `provisioning`, `ready`, `active`, `denied`, `failed`, `needsAttention`, and `revoked` states with actor/time, retry metadata, and completed directory steps. The operation must survive application restart and the user's logout; never implement it as a request-scoped or fire-and-forget task.

Use a hosted background worker with renewable Cosmos-backed reservations and conditional progress updates. Recheck the plant's state and the approving principal's current authority before provisioning/activation. Identify directory objects by stored IDs and enrollment markers. Reconcile uncertain Graph responses before another mutation; an ambiguous create moves to `needsAttention` rather than risking a duplicate identity.

Create the application/service principal, attach only the public certificate, grant the configured API role, and store the plant/workbench identity binding. Activate only after the actual daemon token matches that binding. Directory writes and Cosmos transitions are not one transaction. Revoked approval, missing tenant consent, propagation delay, and partial provisioning must remain visible and non-executable; reconcile any directory side effects before cleanup.

The same mechanism performs approved credential rotation and revocation. A signed-in user need not be a directory administrator, and no operator script is required for normal enrollment. Provisioning endpoints never accept arbitrary Graph requests, application targets, or permission grants.

## Document Layout

Use a common envelope: `id`, `scopeId`, `type`, `schemaVersion`, plant/line identifiers where applicable, timestamps, and document-specific fields. All timestamps used for ownership and ordering are server-generated.

| Partition scope | Documents |
|---|---|
| `plant:<id>:settings` | Plant identity, owner binding, lifecycle/policy, plant/resource-scoped access assignments, and audit records. Not a whole-plant resource directory. |
| `plant:<id>:workbench:<id>` | Admission/identity record, credential operations, separate presence record, execution slot, assignments, claim receipts, results, and mutation audit records. |
| `plant:<id>:line:<id>` | Immutable line revisions, repository/integration settings, coordinator/user grants, and line-to-workbench bindings. |
| `plant:<id>:enrollment:<id>` | Enrollment request, public-key fingerprint, approval/provisioning progress, background reservation/checkpoints, and audit records. |

The same container can hold these document types; separation does not imply a service or container per type. Presence updates touch only presence, not the execution slot or a global registry.

Every resource, enrollment operation, artifact, and query is scoped to its plant. Enforce scope before returning data, including paginated lists and HTML patches. Workbench and assignment identifiers alone cannot cross plant boundaries. Keep plant ownership/access mutations in their settings partition; directory-job progress is transactional with its enrollment record, not with Microsoft Graph.

Each assignment contains its line/revision, target workbench, snapshot reference/digest, submission sequence fields, state, claim reference, heartbeat, publication requirement, and eventual result reference. Keep documents below an application limit of 128 KiB. Store instructions and large evidence in Blob Storage rather than growing the queue document.

An assignment's address is `(plantId, workbenchId, assignmentId)`. IDs are opaque and globally generated by clients; authorization never follows from knowing one. Submission idempotency is scoped to the target workbench and operation. Retrying against a different workbench is a different submission, not a safe retry.

Index the fields needed for queue selection and paginated plant/line views, including type, line, state, submission time, and assignment ID. Exclude large or sensitive payload fields from indexing. Use parameterized queries. Dashboard cross-partition queries are allowed, but do not participate in ownership decisions.

## Queue and Ownership Rules

Cosmos transactions apply within one logical partition. Put a workbench's execution slot and its assignment records in that partition so the one-active-assignment rule is transactional.

For a claim:

1. Authenticate the workbench and validate current line grants and capabilities.
2. Read its admission record, execution slot, and eligible queue entry. Return the prior receipt if this claim ID was already accepted.
3. Submit one transactional batch: conditionally update the admission record using its ETag, conditionally reserve the empty slot, conditionally change the selected assignment from queued to running, create the claim receipt, and append the audit event.
4. On an ETag race, reread and retry within a bounded budget. No model call, Git operation, or other external effect occurs inside this retry.

The admission update acts as a concurrency guard against device revocation. Scoped line-grant changes in other partitions are checked before the transaction but are not part of it; an already-authorized in-flight claim may complete. Do not promise cross-partition atomic revocation.

Completion conditionally checks the matching slot and claim, creates the immutable result metadata, marks the assignment terminal, clears the slot, and appends the audit event in one batch. Identical completion retries return the recorded result; conflicting payloads are rejected.

Publish immutable snapshot/evidence blobs before making database references visible. A failed database write may leave an unreferenced blob, never a successful assignment pointing to an incomplete upload. Verify hashes and enforce tenant/line/workbench ownership on every artifact operation.

Claim receipts survive completion. Retrying a claim returns the original assignment and its current status; it never obtains a different task or instructs a host to replay completed work.

An empty queue check performs no ownership write and does not create an accepted claim receipt. The daemon may retain its intent until a claim is accepted. Presence is reported separately, nominally every 30 seconds; active execution also heartbeats every 20 seconds. Mark stale presence/work for attention after 90 seconds, without freeing ownership.

Start idle checks at five seconds with jitter, backing off to 30 seconds when empty. Requests return immediately; long polling is a later optimization if measurements justify it. Server guidance may tune these values only within published bounds compatible with the presence threshold.

## API Surface

Use `/v1/plants/{plantId}` as the authenticated base `P` below. Define JSON schemas and publish OpenAPI. Reject unknown schema versions and invalid fields rather than guessing defaults.

| Operation | Endpoint and behavior |
|---|---|
| Discovery | `GET /.well-known/plant-config`: public API/protocol versions and expected public authentication metadata. No registry or secrets. |
| Plants | `GET /v1/plants`: paginated accessible plants. `POST /v1/plants`: creator-authorized, idempotent creation and initial owner binding. |
| Plant access | `GET P`, scoped role-assignment operations, and owner transfer: enforce owner/manage-access authority and prevent owner removal without replacement. |
| Enrollment | `POST P/enrollment-challenges`, `POST P/enrollments`, `GET P/enrollments/{id}`: requester-scoped challenge, signed request, and resumable status. |
| Admission | `POST P/enrollments/{id}/approve` or `/deny`: plant owner/registrar decisions. Provisioning/finalization are internal background transitions, not client-controlled directory calls. |
| Activation | `POST P/workbenches/{id}/activate`: verify the approved application-token binding. |
| Credentials | Claim-independent, workbench-bound rotation operations with old/new key proof; owner/registrar-authorized recovery and revocation. Return durable operation status, not immediate directory success. |
| Registration | `PUT P/workbenches/{id}/registration`: update observed machine metadata, installed versions, and capabilities; cannot change identity, admission, or local limits remotely. |
| Presence | `PUT P/workbenches/{id}/presence`: bounded current status, separate from queue ownership. |
| Configuration | `GET P/lines/{id}/revisions/{revision}` and operator-only publication/binding operations: immutable definitions and explicit grants. Workbenches receive only their authorized execution configuration. |
| Submission | `PUT P/workbenches/{id}/assignments/{assignmentId}`: validate grants/capabilities and commit an immutable, targeted assignment. |
| Claim | `POST P/workbenches/{id}/claims` with a persisted claim ID: return the accepted claim/status, or `204` for no eligible work. |
| Heartbeat | `PUT P/workbenches/{id}/claims/{claimId}/heartbeat`: check ownership and return any cancellation request. |
| Result | `PUT P/workbenches/{id}/assignments/{assignmentId}/result`: idempotent, claim-bound completion. |
| Evidence | Claim-bound artifact upload/read and `POST P/workbenches/{id}/events`: bounded evidence and telemetry batches. |
| Web application | Cookie-authenticated HTML pages/actions and Datastar status streams under `/plants`; reuse application operations, not the bearer-only API routes. |
| Operator view | Paginated workbench, enrollment, line, assignment, result, and audit queries, filtered by plant/resource/line authority. |
| Control | Assignment cancellation request, explicit operator resolution, and workbench revocation. Each records actor, reason, operation ID, and resulting state. |

Use a stable operation ID and payload digest for repeatable mutations. Enrollment and provisioning also use ETags for state transitions. Reuse with a different payload is a conflict, not a new operation.

Return Problem Details with a machine-readable error code, safe explanation, correlation ID, and retry guidance. Use `401` for invalid authentication, `403` for insufficient authority, `409` for genuine state/identity conflicts, and `429` or `503` with retry guidance for transient limits/contention. Clients must not treat every conflict as retryable or discard useful error details.

## Cancellation, Failure, and Reconciliation

A cancellation request sets intent; it does not immediately free the slot or prove process termination. The host receives it on heartbeat, stops its owned execution, persists evidence, and reports a terminal result. A disconnected host remains unresolved.

Operator resolution requires inspecting host/process state and external effects, especially an existing branch or PR. Record the evidence and decision before releasing ownership. New work requires a new assignment ID. A claim fence protects service mutations, not GitHub operations already running with local credentials.

During a service outage, a running worker may continue within its local timeout and permissions. It persists its result and retries delivery without rerunning work. Loss of contact alone never causes automatic reassignment. Result replay remains subject to current identity/admission; revocation can therefore require operator reconciliation.

## Configuration, Artifacts, and Telemetry

Server-supplied integration configuration names supported providers, repository targets, publication requirements, telemetry routes, and logical credential references. It contains no workbench private keys, personal tokens, or arbitrary executable plug-ins. Snapshot changes apply only to new assignments.

Initially proxy bounded artifact uploads through the API into private Blob Storage. Apply a configurable limit, initially 32 MiB per artifact, and stream rather than buffer entire uploads. Oversize or truncated evidence is reported explicitly. Do not hand workbenches storage account keys or general container access.

Every request/event carries plant, line, production/run, assignment, workbench, and claim IDs where applicable, plus configuration and package revisions. Measure:

- Submission-to-claim delay and stage durations.
- Online/stale devices, active claims, unresolved work, and result-delivery backlog.
- API latency/error rate, Cosmos RU and throttling, conditional-write retries, and partition growth.
- Verification outcomes, publication read-back, and supported agent/tool/usage events.

The service forwards permitted events to Application Insights using its managed identity. Alternate telemetry integrations are explicit implementations with approved destinations and credentials. Do not route one line's evidence to another line's destination.

Do not export source, prompts, model output, command output, or credentials by default. Detailed evidence requires an explicit policy and access controls. Telemetry is diagnostic; durable assignment/result records remain authoritative.

Commit audit events with ownership/admission transitions in the same partition. Retain them independently of sampled diagnostic traces. Failure to export telemetry must not falsely report a failed coding task or trigger replay; surface the export backlog separately.

## Retention and Capacity

Proposed defaults are 30 days for diagnostic telemetry, 90 days for detailed online terminal records, and 365 days for archived evidence and audit history. Operators must approve deployment-specific retention and legal-hold requirements.

Never TTL an active assignment, execution slot, pending enrollment, or unresolved claim. Archive terminal detail before compacting it. Retain compact identity/claim/idempotency tombstones so old retries cannot recreate work. Blob cleanup must verify that an object is unreferenced; an age cutoff alone is insufficient.

Per-workbench partitions remove whole-plant write contention, not all capacity limits. Monitor logical-partition bytes, RU consumption, and queue depth; revise archival or partitioning before reaching Cosmos limits. Do not claim that horizontal web scaling fixes a hot partition.

Validate 100 and 500 simulated workbenches without model calls, including idle polling, active heartbeats, simultaneous claims, retries, telemetry, result delivery, and operator queries. Run across two API instances and restart one during the test.

Acceptance requires no duplicate ownership or lost terminal results, no cross-line/device access, bounded retries, and successful recovery of interrupted requests. As provisional targets, keep nonempty claim and result acknowledgments below two seconds at p95 under the agreed test mix, excluding polling wait, and record throughput, RU, hosting cost, and throttle/error rates. Set the production capacity envelope from those measurements, not device count alone.

## Delivery

1. Implement plant creation/ownership, scoped authorization, both authentication paths, and the HTML/Datastar application against separate test plants.
2. Build durable server-side provisioning and the installer flow from RFC 001. Demonstrate enrollment by a plant registrar without directory privileges, an operator script, or a continuing user session.
3. Exercise two workbenches producing separate verified PRs, then interruption, revocation, rotation, and the synthetic fleet tests.
4. Verify cross-plant denial, owner transfer, inherited/resource-scoped roles, request-only approval, job recovery without duplicate directory objects, and missing provisioning consent.
5. Verify cookie/antiforgery behavior, bearer-only API responses, shared cookie keys across instances, and authorized Datastar reconnect after role revocation.

No Azure provisioning, identity grants, or package rollout is authorized by accepting these draft documents alone.

## References

- [Cosmos DB transactional batches](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)
- [App Service managed identities](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity)
- [Application Insights Entra authentication](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication)
- [Entra application-role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0)
- [Datastar signals](https://data-star.dev/guide/reactive_signals)
- [Datastar HTML and signal patches](https://data-star.dev/reference/sse_events)
