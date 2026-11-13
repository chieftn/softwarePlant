# Plant Model

## Purpose

The plant supplies and manages the resources that execute production work. A production line organizes workers, workbenches, and production methods to produce and evolve a product. Its versioned metadocument describes that organization; it is not the execution infrastructure.

The initial plant uses two existing Windows devboxes with WSL. Each devbox serves as a VM-based workbench and hosts an agent that receives an assignment and produces an independent PR against `https://github.com/chieftn/productionOutput1`. VM provisioning and multi-worker production coordination are outside this first execution slice.

## Selected Resources

| Resource | Responsibility |
|---|---|
| **.NET** | Application runtime for the workbench host and Microsoft Agent Framework integration. |
| **Microsoft Agent Framework (MAF)** | Common agent abstraction and workflow facilities for composing workers and coordinating production. |
| **Microsoft Foundry Hosted Agents** | Selected managed host for the central coordinator, with Invocations endpoints, platform identity, and durable application state. |
| **GitHub Copilot SDK and its MAF integration** | Coding-agent execution, including the reasoning/tool loop and access to coding tools under an explicit permission policy. |
| **VM-based workbenches** | Equipped execution environments containing the repository workspace, language runtimes, tools, and worker runtime. |
| **GitHub** | Source control, branches, PRs, and associated review and verification results. GitHub Actions is not the plant's dispatch mechanism. |

The initial [workbench implementation](../src/Workbench/README.md) supplies the central coordinator's registration and distribution foundation, a Windows host with an outbound daemon connection, and a WSL coding worker. Azure deployment and production workflows remain separate from this implemented execution foundation.

## Core Concepts

| Concept | Meaning |
|---|---|
| **Plant** | The managed collection of infrastructure and execution capabilities supporting production lines. |
| **Workbench** | An equipped, access-controlled environment in which a worker performs an assignment. The initial workbenches are VMs, not containers. |
| **Workbench Host** | The deployed .NET application on a workbench that receives assignments, prepares execution, runs workers, and reports outcomes. |
| **Worker** | An agent with an occupation, specialization, and assigned role, represented through MAF's agent abstraction. |
| **Assignment** | A bounded task with instructions, input artifacts, acceptance criteria, and expected outputs. |
| **Coordinator** | The component that delivers assignments and tracks execution across workbenches. |
| **Production** | A tracked execution of work using a particular production-line definition and its inputs. It is distinct from a product release. |

## Worker Runtime

The WSL worker creates a Copilot SDK client and uses the MAF integration to expose it as an `AIAgent`. In .NET, the integration package is `Microsoft.Agents.AI.GitHub.Copilot`; `AsAIAgent` wraps the Copilot client.

The local execution path is:

**Windows Workbench Host → WSL worker → MAF Copilot agent → Copilot SDK/runtime → Linux coding tools**

The Copilot SDK's default execution architecture manages a Copilot CLI subprocess over JSON-RPC. The host integrates through the SDK rather than automating an interactive terminal.

Copilot owns the worker's reasoning and tool-invocation loop. MAF supplies the agent abstraction and coordination facilities; it does not introduce a second coding loop around each tool call. Other occupations can use different MAF-supported backends without requiring every worker to use Copilot.

## Empowering a Worker

The host brings together three inputs:

- **Assignment:** what work to accomplish, the inputs to use, and the evidence to return.
- **Production methods:** the worker's instructions, skills, role guidance, and applicable quality requirements.
- **Workbench capabilities:** the repository workspace, executable tools, credentials, and enforced access boundaries.

Instructions do not grant capabilities. A worker instructed to run tests needs a command-execution tool, an installed test runtime, and permission to execute it. The host configures tool availability and permission decisions explicitly. Production methods cannot grant access beyond the workbench's permissions.

Copilot authentication and repository access are separate concerns. The agent runtime needs valid model access; checkout, push, and PR operations need appropriately scoped repository credentials. Neither credential set belongs in the production-line metadocument or assignment payload.

The host runs inside the VM's access boundary. SDK permission handlers govern tool approval but do not replace operating-system isolation. Workspace, network, and credential access remain explicit plant responsibilities.

## Workbench Host Responsibilities

- Receive an assignment addressed to its workbench through an outbound connection to the coordinator.
- Prepare an assignment-specific checkout and branch without modifying another assignment's workspace.
- Configure and start the MAF/Copilot worker with the applicable production methods and tool permissions.
- Report execution progress, failures, and output artifacts.
- Support cancellation and bounded execution, including worker-runtime cleanup.
- Collect verification evidence and the resulting branch and PR references.

The host owns runtime lifecycle and execution boundaries. The worker performs the coding work and permitted repository operations. A successful agent response alone does not establish that verification passed or that a PR exists.

The host executes on Windows and launches the worker through `wsl.exe`. The agent runtime, checkout, Git operations, and verification execute together inside WSL, supporting Linux-oriented products such as 1JS without bridging individual coding tools across operating systems.

## Production Coordination

The distributed execution path is:

**Production coordination → assignment connection → Workbench Host → local MAF/Copilot worker → result**

MAF workflows organize production steps and handoffs. A Foreman worker can reason about decomposition, assignments, and corrective work. Ordinary application code validates those decisions, records execution state, and delivers assignments.

The agent abstraction does not automatically distribute execution across devboxes. The [central coordinator](../src/Workbench/Workbench.Distributor/README.md) implements that connection through Foundry Invocations and Entra-authenticated outbound daemon polling. Authorized workbenches register their machine identity and capabilities. The Foundry durable state store holds registrations, presence, assignments, claims, and results; conditional writes protect ownership across coordinator instances and restarts. Each assignment targets a registered workbench. A missed heartbeat marks work for attention without automatically replaying it.

The first assignments are submitted manually. They exercise the same workbench execution boundary that production workflows use, without requiring a Foreman or a complete workflow engine first.

Foundry hosting and application persistence do not automatically make future MAF production workflows recoverable. Workflow checkpoint/resume and external-effect reconciliation remain explicit requirements. MAF's Durable Extension is an option to evaluate, not an adopted dependency or an automatic property of using MAF.

## First Execution Objective

Each of the two devboxes receives a distinct assignment, runs its local coding worker, and produces a separate PR in `chieftn/productionOutput1`.

The demonstration establishes:

- Which worker instructions, skills, and tools are supplied.
- Which workspace and permissions apply to each assignment.
- How progress and failure are reported.
- How verification evidence and PR references are collected.
- How an assignment is stopped and what artifacts remain afterward.

The workbenches use separate checkouts and branches. They do not merge each other's changes or authorize product release.

## Open Decisions

- Production workflow orchestration, review routing, and automatic workbench selection.
- Service installation, startup, upgrades, and rollback on the devboxes.
- Deployed Foundry identity/recovery verification, workflow durability, and archival beyond the initial small-plant state index.

## References

- [Microsoft Agent Framework: GitHub Copilot integration](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/agent-services/github-copilot)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Copilot SDK integration with Microsoft Agent Framework](https://docs.github.com/en/copilot/how-tos/copilot-sdk/integrations/microsoft-agent-framework)
- [Microsoft Agent Framework Durable Extension](https://learn.microsoft.com/en-us/agent-framework/hosting/durable-extension)
- [Foundry hosted agent runtime contract](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agent-contract)
- [Foundry durable state store](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/agent-state-store)
