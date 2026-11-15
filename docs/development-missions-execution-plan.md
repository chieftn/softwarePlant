# ProductZero: Development Execution Plan

Status: Plan for iterative implementation. Writing this plan does not authorize implementation, deployment, repository publication, machine changes, or model calls.

Companion: [Development missions context](development-missions-context.md).

## Outcome

Define production lines through the Factory web service and persist them in Cosmos DB. Assign a line to approved execution infrastructure. Have an assigned workbench obtain a versioned repository workload, prepare its supported execution environment, and process approved GitHub issues through a bounded Copilot SDK inner loop.

The first configured line is **ProductZero**, using [chieftn/productionOutput1](https://github.com/chieftn/productionOutput1). Its workload lives in the repository's `productionLine` folder. ProductZero is data entered through the supported service operations, not a hardcoded line, startup seed, special repository check, or built-in prompt.

Success means one approved issue produces a verified candidate, execution evidence, and a linked draft PR. The worker does not merge, release, or close the issue as accepted.

## Ownership and Definition

| Location | Owns |
|---|---|
| Cosmos DB through the Factory service | Production-line identity and configuration revisions; independent task/source bindings; workload location; connection references; approved execution policy; infrastructure assignments; mission snapshots, claims, runs, and results. |
| Product repository's `productionLine` folder | Versioned workload definition, Implementation and Alignment instructions, required capabilities, preparation and verification declarations, bounded feedback rules, and expected outputs. |
| Plant service | Access checks, configuration delivery, GitHub issue discovery, task eligibility, snapshot creation, durable claims, credential authorization, and result recording. No model execution. |
| Workbench host and worker | Local permission limits, prerequisite preparation/readiness, workspace and source operations, Copilot SDK sessions, independent verification, recovery, permitted publication, and evidence delivery. |

Cosmos is authoritative for the registered line configuration. Git is authoritative for the referenced workload contents. Store the resolved workload identity and digest with each mission; do not depend on a mutable branch or folder during execution.

The existing Factory service hosts multiple logical plants. This plan uses "assign to a Factory" to mean making the line available through an approved binding to a plant and its workbench resources, not introducing another infrastructure hierarchy. Confirm the actual target plant during the assignment iteration.

## First Design Slice: Configuration Synchronization

Model configuration synchronization before task intake or SDK execution. There are two separate paths:

| Path | Trigger | Outcome |
|---|---|---|
| Configuration synchronization | A workbench discovers a new or changed plant membership, line binding, or line configuration. | Obtain the authorized line configuration and pinned repo workload, prepare the supported environment, and acknowledge the exact configuration it can execute. |
| Mission execution | A configured, ready workbench receives a durable mission claim. | Configure and invoke Copilot SDK sessions from the frozen mission snapshot, then report evidence and results. |

Assignment is not a command to invoke Copilot. A retrieved definition is not proof of readiness. The first synchronization increment must run without claiming an issue, making model calls, or publishing a PR.

### Desired Configuration and Observed State

The service owns the desired configuration and current authorization. The workbench reports what it has actually applied. Do not use one status field to imply both.

Identify configuration by enrollment identity/generation, plant membership version, line binding version, line configuration revision, and resolved workload commit/digest. Reports carry the identity they describe; stale acknowledgments cannot make a superseded or revoked configuration ready. Machine connection/presence remains separate.

| Observed configuration state | Meaning | Permitted progression |
|---|---|---|
| `unconfigured` | No line configuration is applied; no owned execution remains. | An authorized assignment can begin preparation. No claims. |
| `preparing` | The workbench is retrieving/validating the workload or preparing approved prerequisites. | Become `ready`, report `blocked`, or begin stopping on revocation. No claims. |
| `ready` | The exact acknowledged configuration has passed its prerequisite and local-policy checks. | Eligible for claims only while that configuration remains authorized and current. |
| `blocked` | Configuration cannot be applied or used; a specific prerequisite, policy, or validation failure is recorded. | Retry preparation after the cause is resolved, or clear the configuration after revocation. No claims. |
| `stopping` | Revocation has disabled configuration use and cancellation/termination of owned execution or preparation is in progress. | Become `unconfigured` only after stop/cleanup is confirmed; otherwise report `needsAttention`. No claims. |
| `needsAttention` | Termination, ownership, or external effects cannot be established safely. | Authorized reconciliation must establish what stopped and what effects occurred. No claims or automatic reassignment. |

These are configuration observations, not mission execution stages or enrollment lifecycle states. A ready configuration can have an active mission; the mission's durable claim/run record remains authoritative for that work.

The first implementation should demonstrate assigned -> preparing -> ready or blocked, and revoked -> stopping -> unconfigured or needsAttention. Tests may use controlled preparation/execution components; do not claim actual SDK, WSL, or unattended readiness from those tests.

### Immediate Revocation and Hard Stop

For v1, unassigning a workbench from its plant revokes the old plant/line configuration and requires termination of owned in-flight work. Moving the workbench to another plant also revokes its old configuration. Removing the applicable line binding has the same effect for that line. Retirement and permission revocation remain additional reasons to deny execution.

1. The service commits the authoritative membership/binding change. Old configuration is immediately ineligible for new claims and credential grants; old readiness reports cannot restore it.
2. On observing revocation, the workbench stops accepting work, invalidates the old configuration, and cancels preparation and active execution. For v1, it stops owned SDK/worker processes rather than waiting for the task to finish.
3. Preserve the mission journal, result/evidence, and publication references. Mark interrupted work as cancelled only when termination is confirmed; uncertain process state or external effects require attention.
4. Acknowledge the revocation/stop outcome through the machine's Factory connection, even after plant access is removed. Provide narrowly scoped reconciliation/result reporting for its existing claim; do not require the old plant grant or restore general execution authority to record the stop.
5. Clear active configuration and report `unconfigured` only when owned execution is stopped, its outcome is durable, and no unresolved ownership or external effects remain. Otherwise remain `needsAttention`. Do not erase credentials needed for bounded reporting before preserving the outcome; stop using revoked repository credentials.

Unassignment acceptance is not evidence that a remote process stopped. A disconnected workbench may not yet know it was unassigned, and already-issued external credentials or operations cannot be undone by a Cosmos write. Show desired revocation and observed stopping/unknown state separately. Define bounded discovery/control-contact deadlines and local stop behavior before enabling live execution; loss of contact must not permit indefinite execution.

Do not release a claim, automatically reassign interrupted work, or apply another plant's configuration while the old execution is unresolved. Only terminate processes whose ownership the workbench can establish. An unknown process or uncertain push/PR response is a reconciliation case, not permission to kill broadly or replay publication.

Ordinary edits to line instructions/configuration do not rewrite an active mission or imply unassignment. Keep that mission's snapshot fixed; report the new desired revision separately and apply it before claiming subsequent work, without replacing configuration underneath active execution.

### Deferred Quiescing

Add explicit quiesce capability later: stop accepting new missions, let the current mission reach its permitted terminal boundary, then revoke or switch configuration. Quiescing needs an operator-visible state, a completion acknowledgment, a deadline, and an explicit hard-stop override.

Do not implement quiesce implicitly as v1 unassignment behavior. The current requirement is hard stop, including in-flight work; graceful draining is a separate future operation.

## Rules for Every Iteration

- Implement a small usable increment, run its focused checks, and record its outcome and remaining blockers here before moving on.
- Review relevant existing code before implementation. Use the current Contracts, service, Cosmos persistence, Razor pages, and workbench projects; do not introduce one project per domain concept.
- Keep production-line configuration, device-to-plant membership, and mission assignment distinct.
- Task and source bindings are independently selectable. Implement GitHub Issues and GitHub source first; reject unsupported providers explicitly.
- Effective execution authority is the intersection of server-approved policy, workload requests, and local machine limits. Repository instructions cannot grant permissions or install arbitrary extensions.
- Allow one active mission per workbench across all lines. Presence is not readiness, and readiness is not a claim.
- Pin the configuration revision, accepted task content, source commit, and workload revision. Changes affect new missions only.
- Retried operations return their existing records. Cancellation requests stopping; it does not immediately release ownership. Uncertain execution or publication requires reconciliation, not automatic replay.
- Report blockers and failures explicitly. An agent's final response is not proof that checks passed or a PR exists.
- Do not post issue comments, publish repository changes, deploy, enable model execution, or change a test machine without approval for those actions.
- Do not install, repair, or change permissions on the shared development machine to prove workbench readiness.

## Iteration 1: Production-Line Model and Management

**Deliver:** Configurable production-line definitions in the web service and Cosmos DB.

- Examine the existing [production-line schema](../schemas/production-line.schema.json), examples, and service persistence conventions before choosing the contracts. Reuse compatible concepts and explicitly reconcile schema changes; do not create a competing definition silently.
- Define line identity, display name, configuration/schema revisions, source provider/repository/base branch, workload-folder reference, independent task-provider selection, connection references, and approved execution limits/publication policy.
- Separate the stored line record from its plant/resource binding and from individual mission/run records.
- Add authorized create, read, and update operations and corresponding Razor management pages using shared application operations.
- Persist revisioned configuration with conditional updates and the repository's existing audit/idempotency patterns. Keep credentials out of records and payloads.
- Register ProductZero through those operations when the target environment is approved. No runtime seeding.

**Checkpoint:** A user can create and edit a line, reload its persisted definition, and see a concurrency conflict instead of overwriting another edit. A second line works through the same code path. Unauthorized access is denied.

## Iteration 2: Line Assignment and Workbench Discovery

**Deliver:** An approved line-to-plant/resource binding and machine-readable configuration discovery.

- Confirm the target plant and the initial authorized workbench. Support explicit bindings without assuming every workbench in a plant can execute every line.
- Add web/API operations to assign, inspect, and remove a line binding under scoped authority.
- Extend the workbench's existing authenticated assignment discovery to return authorized line configuration and binding revisions.
- Return desired configuration separately from observed applied revision/readiness. Use the configuration state machine above; reject stale acknowledgments.
- Recheck current enrollment, plant membership, line binding, and resource authority. A device in another plant must not obtain the line's execution configuration.
- Model plant unassignment/move and line-binding removal as configuration revocation with hard stop, including in-flight work. Preserve claim ownership until stop and external effects are reconciled.
- Keep Factory-level stop acknowledgment available after plant access is revoked. Define discovery/contact deadlines before live execution; do not promise instantaneous remote termination.
- Keep execution disabled in this iteration; discovery does not imply SDK readiness.

**Checkpoint:** The selected workbench discovers ProductZero after assignment and reports its applied revision or blocker. Unassigned and unauthorized workbenches cannot retrieve it. Revocation invalidates stale eligibility without replacing device identity; controlled in-flight preparation/execution is stopped, and an unconfirmed stop remains visible rather than being acknowledged as complete.

## Iteration 3: Versioned v1 Repository Workload

**Deliver:** A documented, validated workload under `productionLine` in chieftn/productionOutput1.

- Inspect the target repository before selecting preparation commands, verification commands, base branch, or the demonstration issue.
- Specify the v1 folder layout and manifest: schema version, supported workload identifier/version, instruction-file references, required tools, preparation, verification, execution bounds, and requested outputs.
- Define Implementation instructions for one issue and Alignment instructions that assess the exact candidate revision against the frozen requirements and engineering constraints.
- Define separate SDK sessions for the ring boundaries, structured alignment findings, blocking/nonblocking outcomes, and bounded return to Implementation.
- Declare time/iteration limits and failure/escalation behavior. Resolve initial values before enabling execution.
- Keep prompts, checks, and bounded workflow settings in the repo. The installed worker implements the supported v1 transitions; do not build a general workflow engine or download executable plug-ins.
- Validate schema versions, references, paths, supported capabilities, commands, and limits before execution. Repository preparation commands require explicit execution permission.
- Provide authoring guidance and focused validation cases. Publish the folder only through an approved repository change.

**Checkpoint:** The v1 workload validates, invalid or unsupported definitions fail clearly, and changing its instructions does not require recompiling Factory. The manifest identifies what is required to execute this repository rather than a speculative tool inventory.

## Iteration 4: Worker Bootstrap and Readiness

**Deliver:** Assignment-driven preparation of a supported unattended execution environment.

- Establish the service-owned Windows/WSL worker boundary proposed in [RFC 001](../rfcs/001-workbench-workloads-and-installation.md). Validate WSL ownership and startup under the actual runtime identity; an interactive user's working WSL installation is not proof.
- Add the worker executable/package support that is missing from the current Factory solution. Preserve the proposed MAF/Copilot integration unless an explicit design decision changes it.
- Pin compatible worker, SDK, and Copilot runtime versions. Separate approved package/prerequisite installation from repository dependency preparation and ordinary session configuration.
- Resolve the assigned workload at a specific Git revision using authorized read access, validate its requirements, and prepare only supported, approved prerequisites.
- Configure service-owned GitHub access and Copilot model access independently. Factory enrollment grants neither.
- Use a GitHub App installed on explicitly selected repositories. Keep its private key in Key Vault; authorize narrowly scoped, short-lived worker credentials against device, binding, repository, and claimed mission as applicable.
- Permit only bounded bootstrap read access before a claim. Source-write and PR access require publication authorization and a claimed mission.
- Select and verify the unattended Copilot authentication path before calling the workload ready. Do not rely on an operator's interactive login or credential cache.
- Report preparation progress and specific blockers through the existing status path. Readiness requires working tools, credentials, workspace access, compatible workload schema, and local execution approval.
- Fence preparation results by the current configuration identity. Revocation during retrieval or setup cancels owned preparation; a late success must not reactivate the old configuration.
- Make repeated discovery/startup reuse verified prerequisites. Do not reinstall the machine for each mission or carry session state between missions.

**Checkpoint:** On the separate approved test machine, assignment causes preparation and truthful readiness reporting. Missing credentials, unsupported tools, or denied local policy prevent claims. The worker environment is usable without a user session; reboot acceptance is recorded separately.

## Iteration 5: Issue Intake, Mission Snapshots, and Claims

**Deliver:** One approved GitHub issue can become one durable, targeted mission.

- Define the configurable issue eligibility rule and operator approval convention. Do not treat every open issue as authorized work.
- Implement the task provider in the plant service: discover eligible issues, capture instructions/acceptance criteria and task revision/digest, and retain provider identity for progress/results.
- Resolve the source commit and the repo workload at that commit for v1. A mutable base branch is an input to resolution, not an execution identity.
- Build an immutable mission snapshot containing line/binding revisions, task content, source/workload identity and digest, approved execution profile, limits, and expected outputs.
- Store durable task-to-mission bookkeeping, targeted run records, and claims in Cosmos using the existing conditional/transactional persistence patterns.
- Make repeated intake/claim requests return existing records. A changed task is not implicitly another authorized run; require an explicit new attempt after resolution.
- Atomically enforce one active mission per workbench and recheck current admission, binding, and readiness when claiming.
- Add claim heartbeat, cancellation intent, result submission, and attention/reconciliation behavior before enabling execution.
- Keep GitHub labels/comments informational. They are not locks or the authoritative run store.

**Checkpoint:** One approved issue yields one snapshot and one owner despite repeated discovery or concurrent requests. Unready or unauthorized devices cannot claim it. Later issue, line, or branch edits do not alter its snapshot.

## Iteration 6: Implementation and Alignment Execution

**Deliver:** The workbench executes the claimed v1 workload and returns a checked candidate with evidence.

- Validate the snapshot/digest, prepare an isolated mission workspace, and check out the pinned source commit.
- Configure Implementation from the repo workload and frozen task using the supported SDK/MAF integration and explicit tool/permission controls.
- Run workbench-owned verification independently of the agent's success message, then identify the candidate revision.
- Run Alignment in a separate session against that revision. Collect structured findings and verification evidence.
- Return blocking findings to Implementation within the declared bounds. Any changed candidate invalidates prior alignment and receives fresh verification/alignment.
- Keep Copilot responsible for each session's reasoning/tool loop. The wrapper owns explicit transitions, limits, verification, cancellation, and durable evidence.
- Persist local progress and terminal results before delivery. Retry reporting after outages without rerunning work.
- Handle interruption conservatively: resume only when execution and external-effect state are known; otherwise report attention required. Stop only owned worker processes.
- Expose run/ring status, failures, findings, and result references in the web view.

**Checkpoint:** A focused issue reaches a checked candidate; a blocking finding causes bounded revision; exhausted limits and cancellation produce explicit non-success outcomes. Lost result acknowledgments do not cause duplicate execution.

## Iteration 7: Permitted PR Publication and End-to-End Acceptance

**Deliver:** ProductZero produces one linked draft PR from its configured issue workflow.

- Enable publication only for the approved line, repository, claimed mission, and local policy.
- Create the commit and branch using the supported source adapter; create a draft PR linked to the issue.
- Read back repository, branch, commit, base, and draft PR state before reporting publication success.
- Record the checked revision, snapshot identity, verification/alignment evidence, publication references, and terminal disposition.
- Reconcile lost push/PR responses before another mutation. An uncertain publication must not create a second PR or release the task for another worker.
- Report progress/results to GitHub only through explicitly approved actions. Do not automatically merge, mark ready for review, release, or close the issue as accepted.
- Deploy service and worker changes separately with compatibility checks. Roll out to the designated test machine, not the shared development machine.
- Demonstrate the complete path with one approved issue, then confirm that the ready workbench can request the next authorized mission without replaying the completed one.

**Checkpoint:** ProductZero configuration and binding are visible in the website; the assigned workbench executes the pinned workload; the resulting draft PR points to the verified revision; evidence is retained; a repeated request does not duplicate execution or publication.

## Deferred

Graceful quiescing/draining, Foreman/task decomposition, automatic workbench selection, failover of running work, dynamic plug-ins, a general workflow engine, additional providers, parallel ring execution, commitment/merge/release automation, and a custom task-entry UI.

The v1 contracts should not preclude later specialized agents or richer ring workflows, but those capabilities are not prerequisites for the first issue-to-PR path.

## Approval and Iteration Record

Before implementation, agree the first iteration's bounded scope. Obtain separate approval for external repository writes, GitHub App/identity permissions, service deployment, test-machine installation, model calls, and live PR publication.

### Built Configuration-Delivery Increment

The user authorized building production-line definitions, assignments, and workbench configuration discovery. This increment is implemented locally; no service deployment, Cosmos live data creation, repository publication, or test-machine upgrade has been performed.

- The website/API can create and update owned line configuration in Cosmos, retaining immutable revisions and retry receipts. The service registration contract references the repository workload; it does not replace the existing production-method metadocument schema.
- Plant owners can assign a line they own to an active workbench. One line is configured per workbench initially; the same line can bind to multiple approved resources. Membership and binding versions prevent an old assignment from reactivating after a move or unassignment.
- The host discovers desired configuration through its Factory connection, persists it under `runtime\line-configuration.json`, and durably acknowledges its membership/binding/line revision. The website distinguishes desired configuration from the observed acknowledgment.
- Received line configuration reports `blocked`: repository workload retrieval, unattended credentials, prerequisite preparation, and SDK/worker execution are not installed. No line reports `unconfigured`; the service rejects `ready` observations in this slice.
- Revocation clears the local configuration on discovery. Plant membership revocation is applied locally before subsequent configuration/report requests, so a failed request cannot leave the old memo active. Pending acknowledgments are retried or discarded when superseded; old receipts cannot overwrite newer observations.
- The current host starts no worker processes. Actual in-flight SDK/worker termination, conservative claim/effect reconciliation, and bounded contact-loss stopping remain mandatory before enabling execution. Quiescing remains deferred, not substituted for hard stop.
- Uninstall/unenrollment recognize and remove the new owned configuration/report files without widening cleanup scope. The new host requires the service's configuration endpoints; deploy service support before a host upgrade.

Validation: 171 Factory regression tests passed. After final changes, 19 focused management, synchronization, browser, contract, and uninstall cases passed; changed C# whitespace verification and `git diff --check` passed. These checks use the existing isolated test store/HTTP harness, not live Cosmos, GitHub, WSL, or a deployed workbench.

| Iteration | State | Evidence / remaining decisions |
|---|---|---|
| 1. Line model and management | Implemented locally | Contracts, Cosmos records/revisions, owner-authorized API/Razor management, conditional edits and retry receipts. ProductZero is configurable data; live registration/deployment remain unapproved. |
| 2. Assignment and discovery | Configuration delivery implemented locally | Versioned explicit workbench bindings, machine discovery, durable blocked/unconfigured acknowledgments, and configuration revocation. Repo workload retrieval/preparation and real in-flight stop controls remain for the worker iteration. Confirm target plant/workbench and live contact deadlines. |
| 3. Repository workload | Planned | Inspect target repo; settle manifest layout, checks, and execution bounds. |
| 4. Bootstrap and readiness | Planned | Verify service-owned WSL feasibility and unattended GitHub/Copilot authentication. |
| 5. Intake and claims | Planned | Agree issue eligibility/approval convention and durable ownership contract. |
| 6. Ring execution | Planned | Execute against pinned revisions with independent evidence and bounded feedback. |
| 7. PR acceptance | Planned | Approve publication and test-machine rollout; demonstrate one issue-to-draft-PR run. |

Update this record with the actual outcome of each approved iteration. Do not label a design, local build, connection check, or simulated run as deployed end-to-end acceptance.
