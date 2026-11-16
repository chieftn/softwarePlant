# Development Missions: Overnight Implementation Log

Started November 14, 2026, following explicit authorization at 21:21 Pacific.
Baseline: `e0a43b226126c29be46d6dbc9179c050bf497af9` (`updates`).
Existing uncommitted context/plan edits are preserved.

## Scope and Guardrails

Implement local service/workbench support for inherited plant configuration,
authenticated pinned repository specs, and an owned issue -> SDK
Implementation/Alignment -> verified PR -> review/revision -> approval lifecycle.
Keep configuration synchronization separate from mission ownership.

No new GitHub/Entra grants, cloud changes, deployments, test-machine installation,
live SDK/model runs, or live issue/PR mutations are authorized by this work.
Tests use isolated HTTP/store/process/agent fixtures. An operator's `gh` login is
not a runtime credential. Logs contain identifiers, checks, error categories and
decisions, not credentials, private keys, issue bodies or repository source.

## Initial Findings

- Plant-owned configuration discovery and revocation already exist. Extend them;
  do not create individual workbench production-line assignments.
- Factory has no repository connection resolver, workload retrieval, issue claim
  loop or SDK worker yet. The host currently starts no WSL/worker processes.
- The legacy `src\Workbench\Workbench.Worker` already demonstrates the intended
  MAF/Copilot integration with pinned package versions. Reuse that pattern without
  coupling the new Factory runtime to legacy Foundry contracts.
- chieftn/productionOutput1#2 defines the proposed repo workload but is unmerged.
  Its project verification list is empty. Neither a passing authoring validator
  nor this overnight implementation can make that workload executable.

## Gap and Decision Register

| ID | Gap space | Current disposition / required review |
| --- | --- | --- |
| G01 | Unattended GitHub access | Implement GitHub App connection references and narrowly scoped access. Actual app/installation, Key Vault secret references and permissions require operator approval/configuration. No personal-token fallback. |
| G02 | Copilot model access | Require explicit service-owned model authentication and execution opt-in. Keep ambient logged-in-user authentication disabled. Live smoke tests remain gated. |
| G03 | Repository contract and real checks | Draft workload is not on `main`; project checks are unconfigured. Pin and validate retrieved specs; report blockers rather than invent build commands. |
| G04 | WSL ownership/readiness | Preserve Windows host / Linux worker boundary. Package/code checks do not prove WSL under the Windows service identity, installed runtime, sandbox containment or cold boot. No shared-machine installation. |
| G05 | Issue assignment/eligibility | Required labels remain explicit approval/selection gates. Record the chosen targeted mission rule and reject missing authority; do not turn every issue into work. |
| G06 | Review/approval policy | Require configured trusted reviewer identities, commit-specific feedback and an authoritative approval event. Ordinary untrusted comments cannot widen scope. No automatic merge. |
| G07 | Waiting and budgets | Keep review waits durable with no live SDK session. Bound active execution/revision separately from human wait deadlines; preserve ownership while effects are uncertain. |
| G08 | Results and replay | Add typed candidate/evidence/alignment/publication/feedback contracts; bind evidence to exact commits. Persist before reporting or replaying uncertain external operations. |
| G09 | Revocation / in-flight stop | Line removal/replacement and membership changes revoke old execution. Stop only owned processes; unknown process/publication outcomes retain claim ownership and require reconciliation. Quiesce remains deferred. |
| G10 | Defaults and compatibility | New execution remains disabled by default; preserve connection-only installation identity/configuration. New endpoints/package support must be deployed/installed separately. |

## Increment Record

### 1. Baseline and Integration Boundaries

Read current contracts, enrollment/configuration endpoints, Cosmos conventions,
host/reporting loop, RFC 001 and the existing MAF/Copilot runner. Identified
repository authentication and spec resolution as the first implementation
prerequisite. No machine or external resource changed.

Each subsequent entry will distinguish implementation, focused evidence,
remaining gaps, assumptions and operator actions. No simulated result is live
deployment, authentication, model, WSL or publication acceptance.

### 2. Authenticated Pinned Specs

Implemented `POST /v1/enrollments/{operationId}/workload` under exact active
machine authority. It checks the requested membership/binding/line revision
before and after repository resolution. GitHub App installation access is
repository-scoped and read-only; private keys/tokens do not enter the workload
response. No interactive authentication fallback exists.

Resolution follows the exact commit's Git trees/blobs, rejects symlinks and
submodules, enforces bounded UTF-8 files, parses the proposed v1 contract
strictly (including duplicate-property rejection), and hashes frozen inputs.
The workbench retrieves/persists specs after applying configuration revocation.
Late results cannot reactivate a removed/replaced line. Missing connections,
paths or invalid specs remain explicit `blocked` observations.

Focused evidence: **19 passed, 0 failed** across RepositoryWorkloadTests,
EnrollmentWorkloadTests and existing ProductionLineSynchronizationTests.
These are isolated HTTP/Cosmos/local-state fixtures; no GitHub App was
provisioned and no live repository credentials were requested.

### 3. Durable Issue / Review Decisions

Added frozen mission, verification, alignment, publication and feedback
contracts. Implemented the bounded coordinator: Implementation -> exact
candidate -> independent checks -> read-only Alignment -> one PR ->
commit-specific trusted feedback/revision -> authoritative current-head
approval. Publication intent is persisted before writes; uncertain publication
can be reconciled without replaying the agent. Interrupted active work does not
silently restart. Review waits do not retain SDK sessions or reset active budgets.

Focused evidence: **10 passed, 0 failed** in DevelopmentMissionTests.
Tests cover same-PR revision, stale/untrusted feedback, wrong alignment commits,
failed independent checks, revocation, interrupted work, lost publication
responses, original iteration budgets, human deadlines and persistence failures.
The adapters in these tests are simulated; SDK/GitHub/WSL acceptance is still gated.

Safe review rule being implemented for morning review (G06): configured reviewers
only; formal current-head approvals; revision comments must identify the exact
head (`/factory revise <commit>`), or belong to a current-head changes-requested
review. Untrusted/plain ambiguous comments do not automatically run agents.
Draft PRs stay draft until a human changes that state; the worker never merges.

### 4. Service-Owned Issue Claims and Publication Gates

Implemented enrollment-scoped mission endpoints, immutable model/issue/workload
snapshots, conditional workbench reservations and repository-wide unique issue
ownership in the existing Cosmos records container. Task and source repository
connections remain separate. The first task is an explicitly configured issue
number, not automatic queue selection or an inferred GitHub assignee.

Source writes require a persisted candidate publication intent. Git pushes use
an exact expected remote head and ancestry checks; PR read-back requires the
owned branch, base, candidate and mission marker. A lost publication response
can be reconciled read-only on resume, but missing/failed recorded verification
cannot authorize that recovery. No additional PR or agent execution is replayed.

Current membership/binding, source/task connection references and task-selection
labels fence continued authority. Revoked reviewers are filtered by the service
before feedback reaches the worker. The service independently verifies a formal
current-head approval; a worker's assertion alone cannot finish the mission.
Progress retries cannot overwrite newer state. Fault/termination reporting
retains the exact original machine identity after unassignment or retirement
without restoring execution authority.

No automatic merge, issue closure, ready-for-review transition, issue comment
posting, claim expiry or issue requeue was added. Partial reservations and
uncertain external effects remain operator reconciliation cases.

### 5. Actual SDK Adapter and Host / Worker Wiring

Added one Linux execution project, `Factory.Workbench.Worker`, following the
existing worker boundary rather than introducing a separate service or broker.
It uses the repository's existing pinned MAF/Copilot package versions:
`Microsoft.Agents.AI.GitHub.Copilot` 1.28.0 and `GitHub.Copilot.SDK` 1.0.31.
These are real SDK calls, not PowerShell workflow scripts.

The Windows host retains its certificate/Factory connection. An owned
stdin/stdout protocol exposes only authorization, source credentials,
publication/read-back, review and progress to Linux. The explicit model token
is sent over stdin, not arguments or persisted mission files. SDK sessions use
fresh homes and `UseLoggedInUser=false`; GitHub App source credentials are not
used as model credentials.

Implementation receives contained file reads/writes; Alignment receives reads
only. SDK shell, network and unknown permission requests are denied. The wrapper
executes declared preparation/verification separately under explicit local and
line policy. Evidence is bound to the candidate, failed checks are retained, and
Git/checkout invariants are checked around verification and Alignment.

Review waits keep the mission/worker journal but no live SDK session. Active
iteration/time budgets are retained across revisions; human approval has a
separate fixed deadline. Contact loss, authorization failures and invalid review
boundaries preserve ownership instead of restarting agent work.

The host invokes its revocation callback before subsequent workload retrieval,
including when that retrieval fails. Startup attempts reconciliation of the
prior recorded worker even if execution is now disabled. Pending progress and
mission/process journals survive interruptions. Presence reports the owned
mission state separately from configuration observations.

**Stop boundary is incomplete for live activation:** the Linux helper verifies
mission/instance/nonce, live PID and its dedicated process group before signalling
that group. It does not establish that detached descendants remained in the
group. An empty recorded group is therefore not proof that every owned SDK/build
descendant stopped, nor a sandbox boundary. No cgroup/sandbox installation or
physical acceptance was performed. Do not enable this prototype on the shared
development machine or treat a group confirmation as full containment acceptance.

### 6. Focused Validation and Corrections

Final focused run: **46 executed, 46 passed, 0 failed**:
RepositoryWorkloadTests, EnrollmentWorkloadTests,
ProductionLineSynchronizationTests, DevelopmentMissionTests,
EnrollmentMissionTests, CopilotMissionBoundaryTests and PlantApiContractTests.

Additional checks cover service-verified approval, changed connection/selection
policy, reviewer removal, publication intent delivery, missing/failed recovery
evidence, retained failed checks, failed review boundaries, disabled SDK/controller
paths, denied shell access, null publication pipe responses, disconnected pipe
requests, strict local journal round-trips and revocation-before-failed-fetch.
The OpenAPI document includes workload and mission routes/request/response types
and passes the existing parser/validation test.

Two intermediate integration failures exposed the existing strict request rule:
top-level null properties are rejected. Optional progress fields now omit nulls;
nullable positional local journal fields are explicitly serialized. Both
integration regressions and the new round-trip cases pass. A stale workload
digest clears its rejected pending claim request so retrieval can refresh before
the next claim, rather than repeating the same stale request indefinitely.

Changed-C# whitespace formatting and its no-change verification passed using
paths relative to `src\Factory`. The earlier absolute-path formatter invocation
did not cover the intended files; the corrected run formatted 14 files.
`git diff --check` passed after the documentation updates. The final worktree
scope contains only the three development-missions documents and `src\Factory`.

Validation used `--no-restore -p:CopilotSkipCliDownload=true`. Ordinary NuGet
restore followed the new project/package references earlier in implementation;
the Copilot CLI runtime was not acquired or started. No live SDK/model call,
WSL launch, GitHub credential exchange, Git push/PR mutation, cloud operation,
client installation or deployment is represented by these tests.

## Morning Review: Remaining Gates

| Area | What exists locally | What still requires a decision or acceptance |
| --- | --- | --- |
| G01: GitHub authentication | App connection allowlists, installation-owner check, service-resolved PEM key and short-lived repository/purpose-scoped tokens. | Approve/install the app and provision resolved Key Vault-backed settings. Task access uses Issues read; source bootstrap uses Contents read; enabled publication additionally needs Contents/Pull requests write. Source review also reads PR issue comments. No new grants were made. |
| G02: Model authentication | Explicit worker model/token; no logged-in-user fallback. | Establish a supported licensed unattended identity/token and model. `FACTORY_WORKBENCH_COPILOT_TOKEN` is the current host input; secure provisioning/rotation and live auth acceptance remain. BYOK/Entra model-provider support is not implemented. |
| G03: Product workload | Pinned v1 JSON/Markdown loader and fixed typed enforcement. | Review/merge chieftn/productionOutput1#2 and supply real project preparation/verification commands. Its `main` workload is still absent and its proposed verification array is empty. |
| G04/G09: Execution and stop | Owned host/worker pipe, dedicated process-group identity, local journals and conservative uncertain-stop handling. | Package the Linux worker/CLI, prove service-owned WSL startup/cold boot, and establish all-descendant containment/termination. Process groups alone do not satisfy that last requirement. These are activation blockers, not demonstrated capabilities. |
| G05: Assignment | Explicit issue number plus configured labels and Acceptance criteria bullets. | Confirm that targeted issue-number selection is the desired v1 UX, or separately define GitHub assignment/queue selection. No automatic Foreman/scheduler was added. |
| G06: Review | Current trusted reviewers, commit-specific revisions and formal current-head approval; no merge. | Confirm reviewer policy, who changes draft to ready, and the human review convention. Broader review/status UI and comment posting are not implemented. |
| G07/G08: Ownership and results | Durable claims/counters, local candidate/check/alignment evidence, PR receipt and read-only publication reconciliation. | Define operator recovery/requeue for partial reservations, stopped/blocked work and missing publication receipts. Issue claims are retained, not expired. Detailed evidence is local; remote evidence upload/operator retrieval is not yet implemented. Failed verification stops with evidence rather than automatically launching a debugging retry. |
| G10: Readiness and rollout | Disabled-by-default controls, workload blockers and separate owned mission presence. | Configuration observations still do not prove unattended environment readiness. Establish the prerequisite/ready handshake and operator experience before live claims. Deploy the service and approve client packaging/installation separately; installed MSI 0.1.4 remains unchanged. |
| Provider acceptance | Scoped adapter code and isolated positive/policy/serialization fixtures. | Live GitHub negative-response/rate-limit/error-shape acceptance, restart/publication recovery and physical process/output-bound acceptance remain unperformed. No broader upstream or runtime acceptance is implied by the focused suite. |

## Handoff

The local, uncommitted implementation now connects inherited configuration,
authenticated pinned specs, real SDK role adapters and the durable issue-to-PR
review/revision/approval path. It is a gated prototype, not an installed,
deployed or end-to-end accepted production line.

Preserved the existing context/plan wording and added this outcome separately.
No commit or PR was created for these softwarePlant changes. The previously
opened workload PR remains the only published increment. Next approved work
should settle the activation gates above, not switch the enable flags and assume
that credentials, project checks, packaging or containment have been proved.
