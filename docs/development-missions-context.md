# Development Missions: Next-Session Context

Captured November 14, 2026. Design handoff, not authorization to implement or deploy.

## Working baseline

- The tester reports successful Factory enrollment, a running Windows workbench service, and assignment to a plant.
- Before reboot, plant inventory showed `active`, `online`, and fresh presence. `notReady` is expected: the current runtime supports connection/presence, not development execution.
- Post-reboot unattended reconnect has not yet been reported. Upgrade, rollback and uninstall acceptance also remain outstanding.
- Current client: MSI `0.1.4`, runtime `workbench-0.1.4-007`. The service-side provisioning/recovery correction is deployed; no additional directory permissions were granted.
- The test machine is separate from the shared development machine. Do not install, repair or change machine permissions on the development machine.

Installation evidence and artifact references are in `docs\workbench-installation-test.md`.

## Agreed direction

Make the workbench execute bounded development missions according to production-line specifications. Start with a simple task source, not a Foreman or a new broker.

Use **GitHub Issues as the initial task backlog** and GitHub as the initial source provider. Avoid building a custom task-entry/backlog UI first. Retain minimal durable execution bookkeeping in the existing plant/Cosmos store.

Two independent adapter boundaries:

| Adapter | Responsibility | Placement |
|---|---|---|
| Task provider | Discover approved work, read instructions/acceptance criteria, report progress and results. | Plant service |
| Source adapter | Resolve revisions, prepare checkout, manage branches/commits, and perform permitted publication. | Workbench execution components |

Task and source providers must be independently selectable. For example, GitHub Issues could supply work against Azure Repos. Start with explicit GitHub implementations in the existing projects, not a dynamic plug-in framework.

## Minimal model

- **Production-line bindings:** provider identifier, connection reference and provider-specific selection. Task selection and source repository/base branch are separate.
- **Mission snapshot:** accepted task content and revision/digest, pinned source commit, acceptance criteria, approved execution profile and limits. Later issue edits must not silently alter running work.
- **Run record:** task/snapshot identity, target workbench, claim, execution status and evidence/result references.

GitHub labels/comments display status; they are not execution locks. The plant owns authorization, durable claims and results. The workbench owns local permission limits, execution and recovery.

Allow one active mission per workbench initially. Retried requests return their existing records. Cancellation requests stopping; it does not immediately release ownership. Uncertain execution requires attention, not automatic replay or reassignment.

## First two development rings

Run SDK-backed orchestration in the workbench metaruntime:

1. **Implementation:** code, focused tests, debugging and revision.
2. **Alignment:** assess that specific revision against requirements and engineering constraints; produce findings and verification evidence.

Blocking alignment findings feed back into implementation within explicit limits. Copilot owns each session's reasoning/tool loop; the wrapper owns transitions, independent verification and execution boundaries. Do not create competing orchestration loops.

Return a checked candidate and evidence, with a linked draft PR only when publication is explicitly permitted. Do not automatically merge, release or close the task as accepted. Commitment is a later ring.

The existing RFC proposes Windows-host/WSL-worker execution with the MAF/Copilot integration. This discussion did not replace that selection or implement the worker.

## Repository authentication proposal

Use a GitHub App installed on explicitly selected repositories, not a person's interactive `gh` login.

- Plant task integration initially needs Issues read/write; Contents read where required.
- Keep the app private key in Azure Key Vault. The plant uses managed identity to access it and obtains short-lived installation tokens.
- For worker access, the proposed plant credential path checks the device, claimed mission and repository before providing a narrowly scoped token. Never distribute the app private key.
- Request source-write/PR permissions separately when publication is enabled. Repository-scoped tokens do not themselves restrict a task or branch; local policy and branch protections still apply.
- Credentials are referenced by connection identifiers, never embedded in task or mission payloads, logs or Git remote URLs.

This integration is not implemented. Factory enrollment grants neither GitHub access nor Copilot model access; service-owned credentials and an equipped unattended worker environment remain prerequisites.

## Next bounded planning step

Define the smallest GitHub task/source bindings, normalized mission snapshot and durable claim/run contract. Then plan one approved issue reaching one workbench and returning evidence through Implementation/Alignment. Establish workspace, tools and unattended authentication before enabling claims.

Keep the Foreman, automatic scheduling and additional providers deferred. Obtain explicit implementation/deployment approval after the plan is agreed.

Related documents: `docs\architecture.md`, `rfcs\001-workbench-workloads-and-installation.md`, `rfcs\002-plant-service.md`, and `rfcs\003-factory-solution-and-projects.md`.

The iterative delivery plan is in [ProductZero: Development Execution Plan](development-missions-execution-plan.md).

The first design slice is configuration synchronization, before issue claims or SDK execution. Plant unassignment/move and applicable line-binding removal revoke the workbench configuration and require stopping owned in-flight work. Stop confirmation and uncertain external effects remain separate from accepting unassignment. Graceful quiescing/draining is deferred; see the plan's state-machine design.

The configuration-delivery increment is implemented: Cosmos-backed owned line definitions/revisions and host discovery/persistence/acknowledgment. The initial workbench-scoped line assignments were incorrect; the user authorized correcting them to plant-owned assignments inherited by active enrolled workbenches. That correction is deployed as `16a014da-4165-4f06-b61c-f97dfae5fd3b`, verified at 20:33 Pacific on November 14, 2026. The initial deployment at 17:32 Pacific (`984a5ecf-6143-4a98-9949-07683fcb131a`) and the save-form deployment below are superseded. Live routes and preserved settings/identities/network/auth were verified. The host has not been upgraded on the test machine. Received lines will report `blocked`, not ready; repo workload retrieval, prerequisites, credentials, SDK execution, and real in-flight stop controls remain to build. Authenticated live line CRUD and ProductZero registration remain pending. The execution plan records the implementation and deployment evidence.

Corrected assignment flow: open the plant, select **Manage production line**, and assign the line once. Current and later-assigned workbenches inherit it; their detail pages show desired configuration and per-device acknowledgment. Plant line removal/replacement revokes every member's old configuration. Device unassignment/move revokes only that device's old configuration; new membership inherits the destination's current line. Legacy workbench-partition bindings are retained but ignored, never automatically promoted to plant-wide authority. Explicit plant assignment is still required; service deployment is complete, but the first configuration-discovery host upgrade needs separate approval.

The tester then exposed a create-form bug: its empty hidden `Revision` was incorrectly required by MVC. That exact browser submission now passes; edits still require a revision. The approved web-only correction is deployed as `6ef2d3d3-aaac-4d13-8a0b-9763b77b039f` at 20:05 Pacific on November 14, 2026, with settings/identities/network/auth preserved. Live creation awaits the tester's retry. Package/evidence: `src\Factory\artifacts\service\production-lines-savefix-20261114-001`; worker unchanged.

Latest package/evidence: `src\Factory\artifacts\service\production-lines-plantassign-20261114-001`, source commit `daf3d5bd5f1f2241c930d8d3399f76cc719df4c0`. The approved combined deployment also includes repository-input whitespace trimming and field-specific errors. Health is 200, the plant binding API is 401/Bearer, the removed workbench binding API is 404, and browser pages/Entra code/PKCE callback remain protected and correct. All 24 settings, two identities, hosting, VNet/subnet/route-all and authentication fingerprints match preflight. No workbench installation, data creation or new grants were performed.

The user deferred client packaging to focus on the repository workload, then approved defining v1 and opening a draft PR. [chieftn/productionOutput1#2](https://github.com/chieftn/productionOutput1/pull/2) is open against `main`, head `c3c634857992dde48faf3e51538f649f88185769`. It contains the manifest/schema, Implementation and read-only Alignment instructions, bounded workflow/evidence/stop rules, and PowerShell authoring checks (22 assertions passed). The repository began with only a README; product preparation/verification commands are not invented, and missing project verification explicitly blocks execution. This is a proposed contract, not an installed SDK worker or a replacement registry schema. The PR is not merged; `main` does not yet contain the workload. No CI permission expansion, service deployment, client install or model calls occurred in this increment.

Before stepping away, the user specified the next work: extend the workbench's existing plant-line memo discovery to retrieve pinned repository specs through unattended authentication, then consume an approved assigned issue, run the SDK workflow, publish one PR, monitor authorized feedback, revise/verify/push on that PR and await approval. Keep task eligibility/claims in the plant service and SDK/source execution in the workbench. The repository's PowerShell scripts validate authoring only; JSON/Markdown must be translated by our .NET SDK adapter, not executed as a PowerShell agent workflow. Repository credentials and Copilot model access are separate; the interactive CLI login used for this PR does not equip the Windows service.

The execution plan records the requested review/revision loop. Issue assignment, approved reviewers/feedback events, authoritative approval, durable review-wait/claim behavior, active-revision limits, typed results and real project verification remain decisions before enabling it. No new grants, model calls, client installation or deployment are authorized merely by recording this handoff.

## Overnight Implementation Handoff

The user subsequently explicitly authorized overnight local implementation and
detailed gap logging. The inherited-line workbench now has authenticated pinned
spec retrieval, service-owned issue claims/publication/review endpoints, a real
Linux MAF/Copilot SDK adapter and durable Implementation/Alignment ->
verified draft PR -> trusted feedback/revision -> current-head approval control.
This work is uncommitted and disabled by default; it has not been deployed or
installed. The installed client and active service deployment above are unchanged.

The focused local suite passed 46 cases, including OpenAPI validation, policy
revocation, unique claims, strict journal/request serialization, publication
reconciliation and disabled execution boundaries. These fixtures are not live
GitHub/Copilot/WSL acceptance. The process-group stop helper does not prove
all-descendant containment, configuration observations do not yet prove
unattended readiness, and the product workload still lacks real verification.

See [the overnight log](development-missions-overnight-log.md) for implementation
evidence, exact credential/configuration boundaries and the morning decisions:
unattended GitHub/model auth, project commands, WSL/CLI packaging and containment,
issue/reviewer policy, evidence retrieval, operator recovery and separate rollout
approval. PowerShell remains an authoring validator, not the SDK workflow.

## Repository Connection Automation

The user next authorized automating repository access rather than manually
maintaining Key Vault/App Service connection settings. Local Factory changes now
provide a **Repository connections** page: approve a GitHub App manifest, install
it on selected repositories, and verify the resulting connection. The service
stores its registration directly in the existing website vault and persists only
nonsecret connection metadata in Cosmos. Production-line forms select reusable
connections; assigned machines still receive only scoped short-lived credentials.

The initial onboarding is owner-scoped and supports installations on the app's
owning account. A connection can serve many lines/machines; it is not created per
machine. Publication is an explicit setup opt-in, separate from line/machine
policy. Disable/refresh, callback-state checks, vault preflight and interrupted
registration recovery are implemented. Model authentication remains separate.

At the local implementation handoff, rollout still needed service deployment and explicit approval of the one-time
secret get/set grant described in `src\Factory\deploy\README.md` and
`github-connections.production.bicep`. That grant covers secrets in the selected
vault; no Azure permissions, secrets, GitHub Apps or installations were changed
during local implementation. Existing configuration-backed references remain
supported. Live onboarding acceptance has not been performed.

### Production deployment - November 16, 2026

The user approved service deployment and the disclosed vault-wide secret get/set
grant. Both are now deployed. Service deployment
`940198a7-2abf-41ad-9419-9496f4ee9250` is active and runtime-successful at
`https://factory-prod-qm3qqqsg45-web.azurewebsites.net`. The custom role has exactly
secret get/set and is assigned only at `kvojq4jibbz5tc2web` to the existing data
identity. All 24 settings, authentication, identities and hosting/network
fingerprints match preflight. Agent execution remains disabled.

Health/homepage, protected browser routes, Entra authorization-code/PKCE redirects
and bearer-only workload/mission routes passed anonymous live checks. Signed-in
onboarding, service-identity vault writes and GitHub App installation still require
the user's first **Repository connections** setup; they have not been accepted
live. Model access and the installed client are unchanged.

Release evidence is under
`src\Factory\artifacts\service\github-onboarding-20261116-001`. This release includes
uncommitted onboarding changes over `dd875b593b3f3bda077aa5b36740fbcbc41fc21f`;
`package-evidence.json` records source hashes and all 387 verified archive entries.

## Copilot Subscription Authentication

The user chose an existing Copilot subscription for the first model-authentication
path, with an extension point for Entra later. Local changes add **Copilot access**:
one owner-scoped GitHub authorization, used by that owner's production lines and
their currently assigned machines. Its separate GitHub App requests no repository
permissions and leaves the connected `productionOutput1` repository app intact.
App registration, user-token exchange and refresh use the existing website vault;
no manual token/App Service setting or per-machine GitHub login is required.

Workers now request renewable access tokens over their authorized mission pipe,
not `FACTORY_WORKBENCH_COPILOT_TOKEN`. The service checks the exact actively
executing mission and current authority before/after issuing a credential.
Refresh tokens and OAuth client secrets stay in Key Vault. The SDK's
session-scoped provider handles initial/refresh acquisition without ambient
desktop-login fallback. Concurrent refresh has one durable owner, and saved
one-use results can be recovered without replaying exchanges.

Usage remains attributed to the authorized GitHub user's subscription and limits.
OAuth authorization alone is not model entitlement verification; a real SDK
session must confirm access. `IMissionModelAccess`/provider-tagged credentials
leave a small Entra extension point, but only GitHub Copilot is implemented.
The host/Linux worker changes are local and not installed. The user separately
approved deploying the service-side sign-in flow; deployment
`98c21c77-d7e7-4d2f-b47c-022ba4846079` is active and runtime-successful at the
production URL above. It supersedes the repository-onboarding service package.
All 24 settings, authentication, identities and hosting/network fingerprints
match preflight; the existing vault grant was reused with no new permissions.
Health/homepage, the new navigation, protected Copilot/browser pages, Entra
code/PKCE and bearer-only model-credential routes passed anonymous live checks.
Signed-in Copilot authorization and real SDK entitlement/renewal remain
unverified. Agent execution remains off and no live model call was made.
Local validation passed 111 affected OAuth, refresh/recovery, mission, SDK-boundary,
repository, browser and API-contract cases. Scoped whitespace verification and
`git diff --check` passed. These checks do not replace authenticated GitHub
authorization or a real SDK entitlement/renewal acceptance run.
Release evidence is under `src\Factory\artifacts\service\copilot-auth-20261116-001`.
The package contains uncommitted authentication changes over
`3d233f3743253f4ee6cb6932f970ca807ab65105`; all 387 archive entries and 76
service/contracts source hashes were verified. The immediately preceding
repository-onboarding ZIP is retained for rollback.

The first live Copilot setup exposed a browser-flow bug: reopening a pending
setup generated a new operation ID and incorrectly required an explicit restart.
The page now offers **Continue setup** with the existing operation ID; restart
is a separate advanced form and expired attempts still require confirmation.
Deployment `913b95d4-63a9-4f51-a650-8ba25d4ea8f6` supersedes the first Copilot
service rollout. Eight focused browser/authentication cases and anonymous live
checks passed, with all production configuration preserved. Evidence is under
`src\Factory\artifacts\service\copilot-auth-resume-20261116-001`. No user setup
record, GitHub App or credential was changed by the rollout itself.

## Continuous issue queue and explicit completion

The user clarified that a line should continually select eligible issues and
advance only after its own PR is **merged**, then explicitly authorized this
implementation. Approval is readiness, not workflow completion.

The repository workload now declares `workflow.completionCriterion` as
`pull_request_merged`, the only supported criterion. Legacy manifests remain
readable without changing frozen JSON/digests, but missing completion criteria
block execution. Issue acceptance criteria and repository verification remain
separate inputs; Factory does not embed package-file-specific validation.

The plant selects oldest-created open issues with all configured labels and
explicit Acceptance criteria bullets, excluding PRs and existing durable claims.
An empty queue is idle, not a failure. Discovery is bounded to five pages of 100;
reaching the bound reports an explicit error instead of pretending the queue is
empty. The host no longer selects a fixed issue number.

Trusted approval moves to `awaitingMerge`; subsequent trusted feedback can still
revise the same PR within the original budgets. GitHub must independently confirm
the exact owned candidate's merge before the plant accepts `completed`. Only
completed work with confirmed worker termination permits the next claim. Closed
without merge, deadline exhaustion and uncertain outcomes retain ownership for
reconciliation. Factory never merges or silently requeues unsuccessful work.

This is a local implementation, not a service deployment, installer release,
product-repository edit, execution activation or live SDK acceptance. The product
workload must declare completion and real verification before an enabled line can
run; no such remote changes are included here.

### Authorized rollout - November 16, 2026

The user chose to retain the current service-side work-management indirection,
including task selection, durable claims and merge verification, while the
worker runs the repository-defined SDK loop. Other work-management providers can
be adapted behind that service boundary later.

With explicit approval, service deployment
`59b0d1c2-7788-4018-8b37-c6455bb8f4fd` reached RuntimeSuccessful. All 24 settings,
authentication, both identities, networking and vault grants were preserved.
Anonymous health/homepage, protected browser sign-in with code/PKCE and the new
queue route's bearer challenge passed. No configuration, grant, product issue or
repository was changed; execution remains disabled.

Unsigned MSI `0.1.8`, release `workbench-0.1.8-queue-001`, is built at
`src\Factory\artifacts\installer\sdk-018-001\msi\en-US\Factory.Workbench.0.1.8.msi`.
All 812 embedded files/four cabinets and 527 runtime files, including 297 worker
files, matched the published sources/manifests. The release includes the existing
LF bootstrap repair and guided service-owned SDK preparation. Sixty-nine focused
cases passed; ICE validation was skipped, not passed.

The tester should upgrade in place and run Prepare SDK worker for the new
release, preserving enrollment and the owned Linux environment. The repository
still needs the explicit completion criterion, real verification and eligible
issue inputs before separately authorized activation. No installation, WSL/SDK
runtime or model call was performed on the shared build machine.

Evidence: `src\Factory\artifacts\service\continuous-queue-20261116-002` and
`src\Factory\artifacts\installer\sdk-018-001\release-handoff.json`.

## Managed workbench activation - November 17, 2026

The tester subsequently reported successful SDK worker startup and full Linux
shutdown under the service identity. That is preparation evidence, not proof of
model entitlement or a completed workflow.

The user rejected manual client JSON/cloud configuration and authorized the
workbench automation increment. Local changes add optional execution settings
to the existing production-line form: model, Git commit author, trusted reviewers,
review/merge deadline and enablement. Assigned machines resolve these settings
through their existing configuration poll. Repository and Copilot authority
still comes from the line owner's existing connected accounts; no private key,
refresh token, personal CLI login or Windows impersonation moves to the client.

Setup now offers **Start line...**, which displays the actual assignment and
requests explicit local shell/publication consent, and **Stop line**, which
revokes consent and stops execution while retaining unresolved mission journals.
Start checks a fresh matching assignment, pinned workload verification/completion
and prepared worker before saving permissions. Runtime readiness and current
repository/model authority are rechecked before a claim. Membership, line binding
or repository changes require renewed consent. Execution-policy changes revoke
owned mission authority instead of silently changing its frozen inputs.

An unset service development-enable setting allows explicitly enabled managed
lines, not legacy lines; explicit `false` remains a global operator shutdown
override. No rollout may silently remove that override. The implementation and
Windows Setup UI are locally verified, but no replacement MSI, deployment,
installed-client update, activation or model call was performed for this increment.
Product verification commands and eligible issue inputs remain required.

## Retooling exploration and authorized release - November 17, 2026

The user identified a gap between assigning a production line and making each
workbench safely authorized and equipped to run it. Retooling must account for
old owned work, approval of target permissions/configuration, tool and credential
changes, readiness evidence and failure/recovery. Existing assignment, consent,
preparation and stop checks provide pieces, not a complete retooling operation.

Keep the current plant-wide binding and local Start/Stop implementation. No
central operational control, blanket enrollment delegation or remote PowerShell
configuration command is selected. Independent device administration and
short-lived approval of specific add/remove/retool changes remain alternatives.
A temporary management grant need not make the resulting assignment temporary;
expiry alone is not proof that an offline device stopped.

The user explicitly authorized three next tasks: record this gap in documents,
context and progress notes; deploy the updated existing Azure service; then
build a new MSI. This does not authorize enabling execution, expanding grants,
mutating the product repository, installing on the shared build machine or
implementing a retooling subsystem. Record the actual rollout/package outcome
below rather than treating this authorization as evidence of completion.

See [architecture](architecture.md#retooling-gap-between-assignment-and-execution),
[execution plan](development-missions-execution-plan.md#retooling-gap---november-17-2026)
and [security architecture](security-architecture.md).

### Completed release - November 17, 2026

The authorized code-only service update reached RuntimeSuccessful as deployment
`cfca26c6-d010-41a7-8d52-9f9e5ff4f279`. All 24 settings, browser/API auth,
both identities, networking and vault grants matched preflight. Anonymous
health/homepage, protected browser/OIDC/PKCE and bearer-only API/queue checks
passed. The service remains self-contained Linux with .NET/ASP.NET Core
`10.0.15`; all 387 archive entries matched the publish tree.

Unsigned MSI `0.1.9`, runtime `workbench-0.1.9-managed-001`, is built at
`src\Factory\artifacts\installer\managed-019-001\msi\en-US\Factory.Workbench.0.1.9.msi`.
It is 350,880,080 bytes, SHA-256
`44AF45FD3F40CF4BA718524DF3B495199171B4A3C7661D3D3D77940D47FBB24D`.
All 812 embedded files/four cabinets, 527 runtime files and 297 Linux worker
files matched sources/manifests; helper, production profile and pinned
Ubuntu/Copilot payloads matched. The 155 focused Release checks passed.
ICE was skipped, not passed; no installation or runtime/model execution occurred
on the shared build machine.

The release keeps local Start/Stop and assignment-scoped consent. No grant,
setting, product repository, line configuration or execution consent was
changed/enabled. No remote retooling, central Start/Stop or blanket enrollment
delegation was implemented. The gap is now recorded in architecture, execution
plan, security documentation and progress notes. Dedicated-machine upgrade/
preparation, real product checks and eligible issues, explicit activation,
model entitlement/renewal and complete workflow acceptance remain outstanding.

Evidence: `src\Factory\artifacts\service\managed-execution-20261117-001`
and `src\Factory\artifacts\installer\managed-019-001\release-handoff.json`.
No source commit or push was requested/performed for this release.

## First ignition and retained-mission diagnostics - November 19, 2026

The tester installed/prepared 0.1.9, configured and enabled the line, assigned
the plant binding and granted local Start line consent. The first-run verification
setup in chieftn/productionOutput1#5 is merged; issue 3 has the `approved` label
and package acceptance criteria. A stale task connection reference
`github-productzero` was replaced with the connected managed integration.

Setup then reported an owned Linux MAF/Copilot worker as busy. Fresh Factory
presence subsequently reported `notReady` with
`Owned mission requires reconciliation (needsAttention); no automatic replay.`
This is a runtime/recovery state, not an LLM asking an interactive question.
No draft PR or successful SDK/model/verification/alignment completion was verified.
Preserve the claim and journals; do not restart or replay the mission blindly.

Investigation found that the delivery/status path omits the saved failure reason,
worker stderr is discarded, and the previous diagnostic archive excludes mission
state. The user explicitly authorized read-only retained-state export under the
service identity and a new MSI, without permission expansion, Factory deployment
or mission recovery.

Unsigned MSI `0.1.10` / `workbench-0.1.10-diagnostics-002` is built and statically
verified. All 812 embedded files/four cabinets and both runtime inventories
matched; 57 focused checks passed. The updated **Save diagnostics...** reads a
bounded, identity-checked retained state, exports an allowlisted/redacted report,
and displays state/reason/counters. It does not run agents, prepare the new Linux
payload, change consent or release ownership. Already-discarded output remains
unavailable. Dedicated-device installation/export and subsequent diagnosis are
outstanding; recovery still requires separate authorization.

See the [installation handoff](workbench-installation-test.md) for the exact MSI
path/hash and diagnostic procedure. The first issue's fixed verification rule
remains a scaffold; mission-specific verification/task-owned guidance is recorded
as technical debt in the [execution plan](development-missions-execution-plan.md#technical-debt).

### Authorized telemetry revision and explicit retry - November 19, 2026

The dedicated-device 0.1.10 export succeeded: issue 3's mission
`b62b8397-074e-47d9-ad2a-ef1aeea03e8c` was `needsAttention`, with one iteration,
5,688 active milliseconds, no recorded candidate/verification/alignment and no
publication intent. The stored reason was only `Mission boundary failed
(IOException)`. Local fault injection showed that SDK/implementation,
post-implementation authorization, commit and candidate-report failures can
produce that same evidence. The SDK wraps remote RPC errors in `IOException`;
the host and worker also discard HTTP failure detail/code. Historical Azure
log reads yielded no usable matching request records. The actual cause remains
unproven.

The user explicitly requested revising telemetry so the mission can be rerun with
proper evidence. Unsigned MSI 0.1.11 / `workbench-0.1.11-telemetry-001` is built
and statically verified. It adds durable redacted worker/host telemetry, a live
read-only Setup viewer, underlying error/RPC/HTTP/Git detail and diagnostic export.
Its **Retry retained mission...** action is explicit, preserves the old attempt,
uses a fresh checkout of the same frozen mission, retains the claim and remaining
budget, and refuses unresolved commits/publication or changed ownership/authority.
The existing Factory API restart path is covered by a focused test; no Factory
deployment or new permission is needed.

All 71 focused checks and the GUI build/scoped formatting passed. All 812 MSI
files/four cabinets and runtime payload inventories matched. No shared-machine
installation, WSL/SDK/model execution, source commit/push or live mission retry
was performed. Dedicated-device upgrade, updated SDK-worker preparation and
explicit instrumented retry are next. See the current
[installation handoff](workbench-installation-test.md) for the exact artifact and
procedure. This is instrumentation and a controlled retry, not proof that the
unknown live failure has been fixed.

### Retry acknowledgement shutdown fix - November 19, 2026

The dedicated-device 0.1.11 retry passed retained-state integrity at 06:27:59 UTC,
then the host worker pipe was canceled during authorization. The worker reported
Windows host disconnection; no SDK startup or implementation was observed.
Investigation found both Setup and the host deleting the shared retry request
after acknowledgement. Two focused regression cases failed on that behavior.
Its cleanup race can throw an `IOException` after accepting retry, causing the
host's existing connection-failure handler to stop the worker. The supplied
telemetry does not contain that initiating exception, so the exact live trigger
is not independently confirmed.

MSI 0.1.12 / `workbench-0.1.12-retry-fix-001` fixes this concrete defect with the
host as sole request cleanup owner, refuses queued-request replacement and
handled-request replay after restart, records the exception initiating host
shutdown before pipe cancellation, and preserves the UI's retry acceptance
detail. Attempt metadata now changes when preparation creates the new checkout,
before any Git command, rather than before initial authorization. Recovery of
the missing 0.1.11 checkout requires matching pre-attempt counters and bounded
telemetry proving no preparation, no other attempt directories, and unchanged
owned original Git state. Ambiguous effects still require reconciliation.

The original frozen mission, claim, old checkout/state, limits and approval
deadline are unchanged. All 71 focused checks passed, as did the GUI build and
scoped formatting. All 812 embedded files/four cabinets and runtime inventories
were statically verified. The exact MSI and corrected
**Stop line / Prepare SDK worker / Start line / Retry / View telemetry** procedure
are in the [installation handoff](workbench-installation-test.md).
No Factory deployment, new grants, source commit/push or shared-machine runtime
execution occurred. The next dedicated-device result is still needed; neither
the original error nor end-to-end model/PR/merge success is established.

### Confirmed installer regression and saved resumption state - November 19, 2026

The 0.1.12 installer failed on the dedicated device. Error 1722 identified
`ApplyRuntime`. Earlier setup logs showed failed activation at 06:56:19 UTC
and failed rollback at 06:56:39 and 06:58:23 in
`MachineCertificates.GrantServiceStateAccess`. The latest apply attempt stopped
at `Installed()` because the earlier update was still pending.

The confirmed regression was that telemetry introduced
`runtime\mission-telemetry`, while the service-state ACL guard rejected every
runtime subdirectory. Both update and rollback therefore rejected the telemetry
layout. Static MSI payload verification and fake-service tests had not exercised
that guard with existing mission telemetry.

The user explicitly requested fixing the bug, rebuilding, and retaining the
steps/status for later. The last supplied installation state is `updating`,
active `workbench-0.1.11-telemetry-001`, pending
`workbench-0.1.12-retry-fix-001`, previous 0.1.10, with run intent true.
The journal is an `update` from installed 0.1.11, transaction
`{DCED133B-E96D-4B12-9888-76535AF719D7}`. Mission
`b62b8397-074e-47d9-ad2a-ef1aeea03e8c` remains unverified; its retry canceled
during host authorization before SDK startup. Do not infer implementation,
verification, PR or merge success from retry acceptance.

MSI 0.1.13 / `workbench-0.1.13-installer-fix-001` is built and statically
verified. Its guard permits only the owned flat telemetry directory and regular
files, still rejects other directories/links, and preserves the existing private
ACL policy and evidence. Its MSI apply path first uses the validated prior
update journal's existing rollback operation to restore the old selection, then
applies the new release; another installation's journal and ambiguous maintenance
are refused, and explicit stop intent is preserved. No mission reset or replay
is added. Lifecycle tests now invoke the same runtime-layout validator.

Two failure reproductions failed before the fix; 78 focused checks passed
afterward, with a clean GUI build/scoped formatting and all 812 embedded
files/four cabinets verified. No shared-machine MSI/helper/service/WSL/SDK/model
execution, Factory deployment, new grants or source commit/push occurred.
The [installation handoff](workbench-installation-test.md) and 0.1.13
`release-handoff.json` retain the exact artifact, last device state and next
steps: install 0.1.13 in place, Stop line, Prepare SDK worker, Start line,
explicit Retry, View telemetry, then Save diagnostics after shutdown.
Do not retry 0.1.12 or delete/edit state. The dedicated-device installation
and original mission outcome remain to be verified when the user resumes.

### Confirmed retry cleanup permission failure - November 20, 2026

The tester installed 0.1.13 and reached Start line and explicit retry.
At 17:32:56 UTC the retry passed integrity and recovered the unprepared earlier
attempt, but immediately disconnected during host authorization, before SDK
startup. The local crash log supplied by the tester at 17:34:44 UTC confirmed
`UnauthorizedAccessException` deleting the installation-root
`mission-retry-request.json` in `RetryAsync` cleanup. The service only has Read
access there. The fatal exception bypassed the inner connection handler, so
the previous `host-stop` instrumentation did not capture it. The earlier
cleanup-race diagnosis/fix was incomplete; it did not solve this permission defect.

MSI 0.1.14 / `workbench-0.1.14-retry-access-001` moves the shared retry
request into the existing service-writable runtime directory, without expanding
permissions. The old root request is retained as evidence, not migrated or
automatically replayed. A restricted-current-token Windows test reproduces
the actual denied deletion before the change, then passes afterward while
installation.json remains protected and the old request remains intact. Fatal
outer host exceptions are now passed into stop telemetry before pipe cancellation.

All 79 focused checks passed; GUI build/scoped whitespace verification passed.
All 812 embedded MSI files/four cabinets, 527 runtime and 297 Linux worker files
were statically verified. No shared-machine MSI/helper/service/WSL/SDK/model
execution, permission expansion, Factory deployment or source commit/push occurred.
The [installation handoff](workbench-installation-test.md) and current release
handoff save the exact artifact and next steps: install 0.1.14 in place,
Stop line, Prepare SDK worker, Start line, Retry once, View telemetry, then
Save diagnostics after the worker stops. Actual dedicated-device 0.1.14
installation, SDK implementation and PR/merge completion are not yet established.

### SDK reached; token callback host mismatch - November 20, 2026

The dedicated-device 0.1.14 retry at 17:57 UTC passed authorization, credentials,
preparation and SDK startup. It reached SDK session creation, then our
`CopilotModelAuthentication.TokenAsync` rejected the callback host before any
Factory model-credential request: `Copilot model credentials can only be used
with github.com.` The resulting SDK RPC error is -32603. Iteration 2 was consumed;
the trace reports 11,657 total active milliseconds. This proves progress beyond
the prior cleanup crash, not model completion or a candidate commit.

The pinned SDK forwards its effective GitHub host to the callback unchanged;
its runtime contract uses host URLs. Our bare-host comparison rejected HTTPS
origins. MSI 0.1.15 / `workbench-0.1.15-sdk-host-001` accepts only the bare
`github.com` and exact HTTPS origin with optional final slash, case-insensitively.
It still rejects other hosts, HTTP, user-info, alternate ports, paths, query or
fragment before acquiring credentials. Model/expiry validation and Factory's
renewable credential flow are unchanged. The telemetry did not record the actual
callback string, so the exact live string is not independently known.

The three HTTPS-origin cases failed before the fix; all 24 focused SDK boundary
checks passed afterward. The package build and scoped formatting passed, and all
812 embedded files/four cabinets and runtime/native payloads were statically
verified. No shared-machine SDK/model/WSL/MSI/helper/service execution, Factory
deployment, grant change or source commit/push occurred. The exact artifact and
saved steps are in the [installation handoff](workbench-installation-test.md):
install 0.1.15 in place, Stop line, Prepare SDK worker, Start line, Retry once,
View telemetry, then Save diagnostics after shutdown. Preserve iteration 2 and
all original frozen limits; do not reset consumed budget. The live session/model
result, original discarded error, candidate/PR/merge remain unverified.

### First live mission PR merged - November 20, 2026

The 0.1.15 dedicated-device run reached iteration 3, committed a candidate,
completed declared verification and candidate integrity, and entered Alignment.
The user then reported the generated PR and its merge. Read-only GitHub checks
confirmed [chieftn/productionOutput1#6](https://github.com/chieftn/productionOutput1/pull/6)
(`Package File Missing`) merged at 18:27:45 UTC, merge commit
`93282eab33e208609553e9701e97596f1a58eee3`. Its branch
`factory/mission-b62b8397-074e-47d9-ad2a-ef1aeea03e8c` identifies the retained
mission; candidate HEAD was `248c3f1b7fc408e6b4597ff7d9568f781633b6df`.
GitHub returned no closing issue references, so issue auto-closure is not
established.

This confirms the first live mission produced a PR that was merged. Factory's
observation of merged completion and confirmed worker termination remain to be
observed separately; a GitHub merge alone does not establish those final gates.
Leave the running service to perform its normal review/termination reporting;
do not retry the mission. Infrastructure-versus-productive iteration accounting
was recorded separately in the execution plan's Technical Debt section, without
changing this mission's counters or runtime behavior.
