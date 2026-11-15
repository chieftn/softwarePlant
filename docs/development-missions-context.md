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

The configuration-delivery increment is now implemented locally: Cosmos-backed owned line definitions/revisions, explicit plant/workbench line bindings, and host discovery/persistence/acknowledgment. Received lines report `blocked`, not ready; repo workload retrieval, prerequisites, credentials, SDK execution, and real in-flight stop controls remain to build. No live deployment or test-machine upgrade was performed. The execution plan records the implementation and validation evidence.
