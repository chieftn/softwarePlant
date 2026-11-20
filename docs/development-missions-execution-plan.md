# ProductZero: Development Execution Plan

Status: Plan for iterative implementation. Writing this plan does not authorize implementation, deployment, repository publication, machine changes, or model calls.

Companion: [Development missions context](development-missions-context.md).

## Outcome

Define production lines through the Factory web service and persist them in Cosmos DB. Assign a line to a plant; its active enrolled workbenches inherit the configuration. Have an assigned workbench obtain a versioned repository workload, prepare its supported execution environment, and process approved GitHub issues through a bounded Copilot SDK inner loop.

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

The existing Factory service hosts multiple logical plants. The plant owns the production-line assignment; workbenches inherit it through their existing plant membership, rather than receive individual line bindings. This does not introduce another infrastructure hierarchy. Confirm the actual target plant during the assignment iteration.

## First Design Slice: Configuration Synchronization

Model configuration synchronization before task intake or SDK execution. There are two separate paths:

| Path | Trigger | Outcome |
|---|---|---|
| Configuration synchronization | A workbench discovers a new or changed plant membership, line binding, or line configuration. | Obtain the authorized line configuration and pinned repo workload, prepare the supported environment, and acknowledge the exact configuration it can execute. |
| Mission execution | A configured, ready workbench receives a durable mission claim. | Configure and invoke Copilot SDK sessions from the frozen mission snapshot, then report evidence and results. |

Assignment is not a command to invoke Copilot. A retrieved definition is not proof of readiness. The first synchronization increment must run without claiming an issue, making model calls, or publishing a PR.

### Desired Configuration and Observed State

The service owns the desired configuration and current authorization. The workbench reports what it has actually applied. Do not use one status field to imply both.

Identify configuration by enrollment identity/generation, plant membership version, plant's line binding version, line configuration revision, and resolved workload commit/digest. Reports carry the identity they describe; stale acknowledgments cannot make a superseded or revoked configuration ready. Machine connection/presence remains separate.

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

For v1, unassigning a workbench from its plant revokes the old plant/line configuration and requires termination of owned in-flight work. Moving the workbench to another plant also revokes its old configuration. Removing or replacing the plant's line assignment revokes the old configuration for all its workbenches. A joining or returning device inherits the plant's current line under its new membership version, never reusing an old memo. Retirement and permission revocation remain additional reasons to deny execution.

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

**Deliver:** An approved line-to-plant assignment and machine-readable configuration discovery.

- Confirm the target plant. Assign the line to that plant once; active enrolled workbenches inherit its configuration. Readiness, capabilities and local policy still determine whether an individual workbench can execute it.
- Add plant web/API operations to assign, inspect, and remove its line binding under scoped authority, even before any workbench joins.
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

## Iteration 7: Permitted PR Publication, Review/Revision, and Approval

**Deliver:** ProductZero produces one linked draft PR, responds to authorized review feedback with verified revisions, and awaits explicit approval.

- Enable publication only for the approved line, repository, claimed mission, and local policy.
- Create the commit and branch using the supported source adapter; create a draft PR linked to the issue.
- Read back repository, branch, commit, base, and draft PR state before reporting publication success.
- Record the checked revision, snapshot identity, verification/alignment evidence, publication references, and terminal disposition.
- Reconcile lost push/PR responses before another mutation. An uncertain publication must not create a second PR or release the task for another worker.
- Monitor the owned PR for authorized review feedback. Persist review/comment IDs and the reviewed commit; process each accepted feedback snapshot once. Comments cannot grant permissions or silently change approved task scope.
- Re-enter Implementation/verification/Alignment for accepted revision requests, then push the checked candidate to the same owned branch and PR. Do not replay publication, overwrite another actor's commits or treat a stale approval as approval of a changed candidate.
- Wait durably for review/approval without leaving SDK sessions running. Preserve ownership and reporting/revocation capability during the wait; agree claim/capacity behavior before implementation.
- Distinguish bounded active execution/revision budgets from human review wait deadlines. Do not reset iteration limits on each poll or consume an entire execution budget merely waiting for a person.
- Define the authoritative approval signal and approved reviewers. Await that approval; do not infer approval from an ordinary comment or automatically merge afterward.
- Report progress/results to GitHub only through explicitly approved actions. Do not automatically merge, mark ready for review, release, or close the issue as accepted.
- Deploy service and worker changes separately with compatibility checks. Roll out to the designated test machine, not the shared development machine.
- Demonstrate the complete path with one approved issue, then confirm that the ready workbench can request the next authorized mission without replaying the completed one.

**Checkpoint:** ProductZero configuration and binding are visible in the website; the assigned workbench executes the pinned workload and opens one verified draft PR; accepted review feedback produces a fresh checked revision on that PR; the workflow awaits approval; evidence is retained and repeated polling/retries do not duplicate execution or publication.

### Next Requested Workbench Slice

The user requested the next sequence before stepping away:

1. Extend the existing workbench membership/plant-line discovery to retrieve the assigned repository workload. Configuration memo code already exists; do not rebuild a per-workbench line-assignment model.
2. Resolve the connection reference through unattended, scoped repository authentication, obtain the manifest/instructions from one pinned Git revision, and validate the supported contract. The operator's interactive `gh` login used to publish the workload PR is not service-owned authentication. GitHub repository/issue/PR access and Copilot model access are separate prerequisites.
3. Deliver one approved assigned issue as a frozen, owned mission. The plant's task adapter owns eligibility/intake; the workbench worker consumes the snapshot, runs SDK-backed Implementation/Alignment, verifies the candidate and publishes the permitted PR.
4. Monitor that PR, accept authorized revision feedback, revise/verify/push on the same branch and then await explicit approval. Keep the outer durable lifecycle in the workbench/service boundary and the agent reasoning/tool loop in the SDK.

The PowerShell files in chieftn/productionOutput1#2 are **authoring validators**, not an implementation of the Copilot SDK workflow. Its JSON/Markdown contract is our proposed workload format, not a native SDK schema. The .NET worker adapter must translate it into supported SDK sessions/tools and independently enforce permissions, budgets, verification and stopping. The [SDK architecture](https://github.com/github/copilot-sdk#architecture) uses an SDK client and agent runtime (by default a CLI server child process); it does not automatically consume our manifest or supply the issue/PR lifecycle.

Before enabling this path, settle the issue-assignment rule, permitted feedback actors/events, authoritative approval signal, review-wait/claim behavior and active-revision limits. Define the missing typed mission/result contracts and actual project verification commands. Approve GitHub App/credential provisioning, Copilot authentication/model calls, service deployment and test-machine installation separately; no interactive login or ambient credentials may substitute for service readiness.

## Deferred

Graceful quiescing/draining, Foreman/task decomposition, automatic workbench selection, failover of running work, dynamic plug-ins, a general workflow engine, additional providers, parallel ring execution, commitment/merge/release automation, and a custom task-entry UI.

The v1 contracts should not preclude later specialized agents or richer ring workflows, but those capabilities are not prerequisites for the first issue-to-PR path.

## Technical Debt

### Mission-specific verification - November 19, 2026

**Status: open; recorded for follow-up, not authorized for implementation.**

The worker currently executes verification commands frozen in the repository
workload. The first-run setup in chieftn/productionOutput1#5 turns the initial
package issue's acceptance criteria into a line-wide rule: every candidate must
have a private package manifest without dependencies, devDependencies or scripts.
That scaffold would reject later work that legitimately adds those fields.
It is not the general verification contract we want.

Replace issue-specific line-wide rules with bounded, mission-specific verification:
Implementation proposes appropriate checks and adds relevant tests; the worker
executes those checks against the exact candidate and records results; Alignment
judges whether the checks and evidence actually demonstrate the frozen requirements.
Keep stable repository build/test checks mandatory where appropriate.

Independent verification remains required. Agents must not silently disable
checks or declare their own work accepted. Define how a proposed verification
plan is validated, bound to the candidate and retained with its evidence, while
preserving existing execution permissions, budgets and stopping controls.
This requires extending the current frozen-command contract, not merely changing
agent instructions. No runtime, workload PR or execution authority is changed
by recording this debt.

### Separate infrastructure attempts from productive iterations - November 20, 2026

**Status: open; recorded for follow-up, not authorized for implementation.**

The worker increments the mission iteration counter immediately before invoking
Implementation. SDK startup, session creation and credential-callback failures
after that increment consume the same budget as a completed
Implementation/verification/Alignment cycle or a human-requested revision.
In the first live mission, the token-callback failure consumed iteration 2
without producing code; iteration 3 produced the PR. Infrastructure failures
therefore reduced the budget available for useful implementation and review.

Define separate accounting for infrastructure attempts and productive
implementation/revision iterations. Agree the evidence boundary for charging a
productive iteration, how partially executed or failed model work is counted,
and whether human-review revisions share that budget or have a separate limit.
Do not treat every failed attempt as free: bound infrastructure retries, model
usage and total active time, and persist the counters across restarts and retries.

Preserve frozen authority, deadlines, ownership and uncertain-effect
reconciliation. New accounting must not permit automatic replay of ambiguous
execution/publication or unbounded retries. Existing missions retain their
current counters and limits; recording this debt does not reset this mission's
budget or change runtime behavior.

## Approval and Iteration Record

Before implementation, agree the first iteration's bounded scope. Obtain separate approval for external repository writes, GitHub App/identity permissions, service deployment, test-machine installation, model calls, and live PR publication.

### Built Configuration-Delivery Increment

The user authorized building production-line definitions, assignments, and workbench configuration discovery. The web service was subsequently deployed with explicit approval; the host changes remain local. No production-line data creation, repository publication, or test-machine upgrade has been performed.

- The website/API can create and update owned line configuration in Cosmos, retaining immutable revisions and retry receipts. The service registration contract references the repository workload; it does not replace the existing production-method metadocument schema.
- The initially deployed assignment was incorrectly scoped to individual workbenches. The deployed plant-owned correction below supersedes that model.
- The host discovers desired configuration through its Factory connection, persists it under `runtime\line-configuration.json`, and durably acknowledges its membership/binding/line revision. The website distinguishes desired configuration from the observed acknowledgment.
- Received line configuration reports `blocked`: repository workload retrieval, unattended credentials, prerequisite preparation, and SDK/worker execution are not installed. No line reports `unconfigured`; the service rejects `ready` observations in this slice.
- Revocation clears the local configuration on discovery. Plant membership revocation is applied locally before subsequent configuration/report requests, so a failed request cannot leave the old memo active. Pending acknowledgments are retried or discarded when superseded; old receipts cannot overwrite newer observations.
- The current host starts no worker processes. Actual in-flight SDK/worker termination, conservative claim/effect reconciliation, and bounded contact-loss stopping remain mandatory before enabling execution. Quiescing remains deferred, not substituted for hard stop.
- Uninstall/unenrollment recognize and remove the new owned configuration/report files without widening cleanup scope. The new host requires the service's configuration endpoints; deploy service support before a host upgrade.

Validation: 171 Factory regression tests passed. After final changes, 19 focused management, synchronization, browser, contract, and uninstall cases passed; changed C# whitespace verification and `git diff --check` passed. These checks use the existing isolated test store/HTTP harness, not live Cosmos, GitHub, WSL, or a deployed workbench.

### Web-Service Deployment

Initially deployed November 14, 2026 at 17:32 Pacific to `https://factory-prod-qm3qqqsg45-web.azurewebsites.net`. Deployment `984a5ecf-6143-4a98-9949-07683fcb131a` reached `RuntimeSuccessful`; the save-form correction below supersedes it. Source commit: `7e093205291285ea5aa2eb4306a8a3aa1ea6e873`.

The Linux self-contained package passed 15 focused predeployment checks. Health/homepage return 200; the new line, binding, configuration and observation API routes return 401/Bearer without authentication; the production-line page requires sign-in; Entra authorization-code/PKCE and the existing callback remain correct. All 24 settings, both managed identities, Key Vault selector, startup command, VNet/subnet/route-all and Easy Auth configuration were preserved. No directory grants or infrastructure changes were made.

Evidence and package: `src\Factory\artifacts\service\production-lines-20261114-001`. The ZIP is 58,076,155 bytes, SHA-256 `9E67E60B2210C276930B19BDFA027772CDACC2EBA0B2E7E7ED8FDF6DA0250F2D`; all 387 entries match the publish output. `deployment-preflight.json`, `deployment-result.json` and `release-handoff.json` retain nonsecret evidence. Authenticated live line creation/editing/assignment has not been verified; no ProductZero record was created by deployment.

The existing workbench was not upgraded. Worker installation is the next separate step; deployment does not enable SDK execution or issue intake.

### Save-Form Correction

The tester's first line creation failed with "The Revision field is required." The create form submits an empty hidden `Revision`; the nonnullable form property incorrectly made MVC require it. The original browser test omitted that hidden field and missed the real submission.

The form revision is now optional for creation and explicitly required for updates. The regression submits the actual hidden field and also confirms that a revisionless edit cannot overwrite a line. The failing case was reproduced before the fix; two focused browser/revision checks and formatting/diff checks passed afterward.

With explicit approval, deployed the correction at 20:05 Pacific on November 14, 2026. Deployment `6ef2d3d3-aaac-4d13-8a0b-9763b77b039f` reached `RuntimeSuccessful`; the plant-owned deployment below supersedes it. All settings, identities, networking and authentication were preserved; health, protected routes and browser sign-in passed. Authenticated live creation still requires the tester's retry; the workbench remains unchanged.

Package/evidence: `src\Factory\artifacts\service\production-lines-savefix-20261114-001`. ZIP size 58,076,188 bytes; SHA-256 `E586C75ED777731C0BBFEAEA1A2784CBB429BC8E9EB708F52A4702CF9BE639BB`; 387 verified entries. Source is commit `7e093205291285ea5aa2eb4306a8a3aa1ea6e873` plus the uncommitted save-form correction; `release-handoff.json` records the source patch and assembly hashes.

### Repository Input Correction

The next form failure was surrounding whitespace in a repository input. Both repository names were valid; the generic validator message did not identify which field failed. The web form now trims source/issue repository inputs, and validation errors explicitly name "Source repository" or "Issue repository". URL/credential rejection remains unchanged. Three failing regression cases were reproduced, then four focused browser/definition checks and formatting/diff checks passed.

The user declined its standalone deployment, then approved the combined plant-assignment deployment below. The whitespace and field-specific error corrections are now live. No service settings, identities, or workbench installation changed.

### Plant-Owned Assignment Correction

The user rejected individual workbench line assignments and explicitly authorized correcting the model. A production line is now assigned once to a plant. Its active enrolled workbenches inherit the current configuration, including devices assigned later; the plant can be configured before it has any workbenches.

The binding and its audit now live in the plant settings partition, with a conditional plant write and monotonically increasing binding version. Web/API assignment is at `/plants/{plantId}/production-line` and `/v1/plants/{plantId}/production-line`. The plant page owns the management link; each workbench detail shows inherited desired configuration and its own acknowledgment. Enrollment/membership fencing remains separate from the shared plant binding.

Removing or replacing the plant's line revokes the old configuration for all members. Moving/unassigning a device revokes its old memo; joining or returning inherits the destination's current line under a new membership version, not an old authorization. The existing host synchronization already handles this boundary, including a destination with a lower plant-binding version.

The old workbench-scoped assignment routes are removed, not silently widened. Existing workbench-partition records are retained but ignored; no per-device binding is promoted to plant-wide authority. Explicitly assign the plant through the deployed plant page. The configuration-discovery host increment has not been installed; its corrected service prerequisite is now deployed.

Focused production-line, real-client synchronization, browser, connection, Cosmos serialization and OpenAPI checks passed, along with changed-C# whitespace verification and `git diff --check`. Regressions cover assignment before enrollment, multiple distinct machine identities inheriting changes, shared revocation, rejected stale assignments/observations, ignored legacy bindings/routes, cross-plant moves and returns, and the plant-based browser flow. These are isolated checks, not live deployment or SDK/worker termination acceptance.

With explicit approval, deployed this correction and the repository-input fix at 20:33 Pacific on November 14, 2026. Current active deployment: `16a014da-4165-4f06-b61c-f97dfae5fd3b`, `RuntimeSuccessful`; verified UTC `2026-11-15T04:33:21.5352494+00:00`. Source commit: `daf3d5bd5f1f2241c930d8d3399f76cc719df4c0`.

Package/evidence: `src\Factory\artifacts\service\production-lines-plantassign-20261114-001`. ZIP size 58,075,847 bytes; SHA-256 `732947F106836A8F1E8B60F88BEB18B33216318C77DD8F000FFAE73B327E20A1`; all 387 entries match the fresh Release Linux self-contained publish output. Service assembly SHA-256 `799B524B7DE309C073717EC89AB8017C0A13387C2916F562A9AFD6A6D4F44BEC`. `release-handoff.json`, `deployment-preflight.json` and `deployment-result.json` retain nonsecret provenance and verification evidence.

Health is 200; the new plant binding API challenges with 401/Bearer; the removed workbench binding API returns 404; assignment/create pages require sign-in; Entra authorization-code/PKCE and `/signin-oidc` callback are preserved. All 24 settings, both managed identities, Key Vault selector, startup/runtime, VNet/subnet/route-all and authentication fingerprints match preflight. No cloud settings, identities, permissions, data or workbench installation were changed. Authenticated live creation/assignment still requires tester acceptance. SDK/worker execution and quiescing remain deferred.

### Repository Workload v1 Draft

With explicit approval, inspected chieftn/productionOutput1 and opened [chieftn/productionOutput1#2](https://github.com/chieftn/productionOutput1/pull/2) as a draft. The repository initially contained only its README; no product stack or build/test commands existed.

The PR defines `productionLine/workload.json`, its authoring schema, separate Implementation/Alignment instructions, one bounded three-pass/30-minute workflow, independent candidate verification, expected evidence, immutable context and hard-stop/publication boundaries. It documents the distinction from Factory's production-line registry and product/method metadocument. Dependency-free PowerShell 7.4 authoring validation and 22 focused assertions passed; requiring project verification fails explicitly because its command list is not yet configured.

No fake product commands or runtime readiness are claimed. Actual product verification must be agreed and configured; the installed worker must explicitly support the proposed contract and enforce its permissions. The PR is not merged, so Factory's configured `main` source does not yet contain this workload. Client MSI packaging/installation, SDK/worker execution and authentication remain separate pending work.

Published branch: `user/rkessler/production-line-v1-contract`; head commit `c3c634857992dde48faf3e51538f649f88185769`, independently verified on the remote. The draft is open against `main` and contains only eight README/productionLine paths. An optional GitHub CI workflow was omitted rather than requesting additional OAuth workflow permission; the local validation commands remain available. No service deployment, model invocation, worker installation, product implementation or merge was performed in this increment.

| Iteration | State | Evidence / remaining decisions |
|---|---|---|
| 1. Line model and management | Service deployed; live CRUD acceptance pending | Contracts, Cosmos records/revisions, owner-authorized API/Razor management, conditional edits and retry receipts. ProductZero is configurable data; live registration remains to perform. |
| 2. Assignment and discovery | Plant-owned service correction deployed; host upgrade pending | Corrected plant assignment/API/UI deployed as `16a014da-4165-4f06-b61c-f97dfae5fd3b`; old workbench binding route returns 404. Explicit plant assignment and authenticated live acceptance remain. Host discovery/persistence/acknowledgment is built but not installed. Repo workload retrieval/preparation and real in-flight stop controls remain for the worker iteration. Confirm target plant and live contact deadlines. |
| 3. Repository workload | Draft PR opened; contract review pending | chieftn/productionOutput1#2 defines the proposed v1 manifest, two agents and bounded workflow. Authoring checks passed; product verification is explicitly unconfigured. Review/merge, actual project checks and worker support remain required before execution. |
| 4. Bootstrap and readiness | Planned | Verify service-owned WSL feasibility and unattended GitHub/Copilot authentication. |
| 5. Intake and claims | Planned | Agree issue eligibility/approval convention and durable ownership contract. |
| 6. Ring execution | Planned | Execute against pinned revisions with independent evidence and bounded feedback. |
| 7. PR acceptance | Planned | Approve publication and test-machine rollout; demonstrate one issue-to-draft-PR run. |

Update this record with the actual outcome of each approved iteration. Do not label a design, local build, connection check, or simulated run as deployed end-to-end acceptance.

### Overnight Local Implementation

After the planning handoff, the user explicitly authorized overnight
implementation with detailed gap logs. This updates the planned state above;
it does not alter the existing deployment or installed client.

| Increment | Local outcome | Remaining gate |
|---|---|---|
| Assigned workload retrieval | Exact-machine GitHub App-backed tree/blob resolution, strict v1 specs, immutable digest/cache and revocation fencing implemented. | Provision unattended connections; merge/configure the product workload. |
| Issue intake and ownership | Frozen explicit issue/model/snapshot, unique repository-wide issue claim, conditional per-workbench reservation and scoped credential/progress endpoints implemented. | Agree targeted issue UX and operator reservation/requeue policy; no live claim acceptance. |
| SDK rings and review | Actual pinned MAF/Copilot Linux adapter, independent checks/Alignment, publication intent, one draft PR, trusted commit-bound revisions and service-verified approval implemented. | Provision supported model auth, real project checks and explicit publication/reviewer policy; no live model or PR run. |
| Host/worker lifecycle | Restricted owned pipe, local journals, resume read-back and process-group stop/revocation wiring implemented. | Worker/CLI packaging, service-owned WSL/cold-boot acceptance, all-descendant containment and a real readiness handshake remain blockers. |
| Local contract checks | 46 focused cases passed; changed-C# whitespace verification and OpenAPI parser/validation passed. | These are isolated fixtures, not end-to-end runtime acceptance. |

Detailed results and decisions are recorded in
[the overnight log](development-missions-overnight-log.md). Source changes remain
uncommitted; no new grants, service deployment, client installation, live SDK/WSL
execution or repository mutation occurred. Keep execution disabled until the
recorded activation blockers are resolved and the separate rollout is approved.

### Retooling gap - November 17, 2026

The user identified retooling as an exploration gap and requested that it be
recorded before further design. Current assignment changes and local setup do
not form a complete retooling operation.

| Question | Existing piece | Missing coordination or decision |
|---|---|---|
| What must stop? | Versioned revocation, worker termination and retained claims/journals. | Account for owned work and uncertain stop/publication effects before transition proceeds. |
| Who approves the target? | Plant/line ownership, connection authority and assignment-scoped local consent. | Decide whether and how independent device administration approves a reviewed target configuration without visiting every machine. |
| What changes? | Line settings, pinned workload retrieval, prepared Linux runtime and mission credentials. | Determine and apply target-specific tools/configuration/permissions without treating assignment as readiness. |
| What proves readiness? | Release/process-bound SDK preparation checks and separate workload/mission gates. | Report the target as applied, authorized and ready, or name the remaining blocker. |
| What if it fails? | Conservative stop/recovery and durable ownership. | Define interrupted-transition recovery and operator reconciliation without replaying uncertain effects. |

Enrollment identity, plant membership, desired line assignment, device-side
permission and applied tooling are distinct. Today a plant line change affects
all members, stops/revokes old work as observed, and invalidates their local
binding consent; each member needs local Start line for the new assignment.
This remains the chosen implementation for the current release.

The alternatives discussed were durable central delegation and an independent
administrative channel that automates device-side grants. Short-lived,
target-scoped grants for add/remove/retool operations are another open option;
expiry would close a management-change window, not automatically undo the result
or prove offline termination. Exact reviewed-configuration binding versus normal
continuous source/workload evolution is unresolved.

**Authorized next delivery:** document this gap, deploy the existing updated
Factory service and build a matching MSI. Do not implement retooling automation,
central Start/Stop, blanket enrollment delegation or a remote administration
command as part of that delivery. Existing production settings/grants and
disabled execution must be preserved. See the [security architecture](security-architecture.md)
and the dated updates in [context](development-missions-context.md) and
[progress notes](development-missions-overnight-log.md).

### November 17 delivery outcome

The retooling gap is recorded without changing the current design. The existing
updated service is deployed as `cfca26c6-d010-41a7-8d52-9f9e5ff4f279`
(RuntimeSuccessful), preserving production configuration and grants. Matching
unsigned MSI `0.1.9` / `workbench-0.1.9-managed-001` is built and statically
verified; 155 focused Release checks passed. The package was not installed or
executed on the shared build machine, ICE was skipped, and execution remains
off. Exact paths/hashes and outstanding dedicated-machine/model acceptance are
in the [installation handoff](workbench-installation-test.md). No retooling
automation, central delegation, product-repository mutation or source commit/
push was performed.
