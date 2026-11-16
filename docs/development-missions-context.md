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
