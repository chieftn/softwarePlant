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
