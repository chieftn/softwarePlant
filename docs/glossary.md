# Glossary

Shared terms from the [architecture](architecture.md), [plant model](plant-model.md), and workbench and plant-service RFCs. Definitions describe meaning, not implementation status. A domain concept does not require its own service or software component.

## Factory and Production

| Term | Definition |
|---|---|
| **Software Factory** | The overall software production operation, encompassing engineering ownership, production lines, and execution infrastructure. |
| **Plant** | The processing infrastructure, resources, and capabilities available to execute production work. |
| **Production Line** | The jointly owned guidance, skills, worker definitions, role assignments, workflows, and verification requirements used to manufacture and evolve a product. It binds to plant resources for execution. |
| **Plant Binding** | The association of a production line with the workbenches and capabilities approved to execute its work. A binding does not imply dedicated ownership of the infrastructure. |
| **Product** | The enduring software offering produced and evolved by a production line. |
| **Product Release** | A specific version of a product made available to consumers under human authorization. |
| **Bill of Materials (BOM)** | The entry point for production, specifying the inputs and scope of the work. Its exact contents remain unspecified. |
| **Work Unit** | A bounded piece of work decomposed from a BOM, such as a task or bug. |
| **Production / Production Run** | A tracked execution of work using a particular production-line definition and its inputs. It may involve multiple assignments and is not a product release. |
| **Production-Line Metadocument** | The versioned definition of a product's business problem and the line's production methods. BOMs and plant bindings are separate inputs. |
| **Line Revision** | A particular version of the production-line definition. It is distinct from the document's schema version and the product's release version. |

Use **production line**, not **product line**, for the means of production. A product line commonly means a family of related products.

## Workers and Production Methods

| Term | Definition |
|---|---|
| **Worker / Agent** | An agent that performs production work in an assigned role. A worker is not the workbench or the process hosting it. |
| **Occupation** | The kind of production work a worker practices, such as coding, task coordination, or quality assurance review. |
| **Specialization** | A line-specific focus within an occupation, such as security or performance review. |
| **Role** | The responsibilities assigned to a worker within a production line. This domain term is distinct from an authorization role. |
| **Coder** | A worker occupation responsible for implementing work units, adding relevant tests, and addressing review findings. |
| **Task Coordinator / Foreman** | A worker occupation responsible for decomposition, assignments, dependencies, progress, contribution integration, and escalation of blocked work. |
| **Quality Assurance Reviewer** | A worker occupation that assesses contributions and integrated changes against acceptance criteria within a defined review scope. |
| **Production Methods** | The line's injected methods for performing work: guidance, skills, role instructions, coordination rules, and verification practices. They are separate from the BOM. |
| **Guidance** | Instructions and constraints supplied by the production line to direct how workers perform their roles. |
| **Skill** | A named set of task-specific instructions supplied through production methods and referenced by worker definitions. |
| **Tool Capability** | A declared ability required by a worker, such as workspace access, verification, or source control. Declaring a capability does not supply its implementation, credentials, or permission. |
| **Workflow** | The production methods' arrangement of steps, dependencies, handoffs, and decisions. It is not the durable assignment queue. |

Occupation describes the kind of work; specialization describes its focus; role describes the assigned responsibilities. These distinctions do not require separate agent runtime implementations.

## Execution and Bookkeeping

| Term | Definition |
|---|---|
| **Workbench** | An equipped, access-controlled execution environment in which a worker performs an assignment. The plant supplies the environment; the line supplies the worker's role and methods. |
| **Workbench Host** | The application that receives assignments, enforces local execution limits, manages worker execution, and reports outcomes. |
| **Workload** | A kind of work the workbench must support, including its agent runtime, repository tools, dependency preparation, build, and verification requirements. |
| **Assignment** | A bounded execution request with instructions, inputs, applicable production methods, acceptance criteria, permissions, and expected outputs. A work unit describes the work; an assignment specifies an execution of it. |
| **Assignment Snapshot** | The immutable, resolved inputs and configuration used for one assignment, including the line revision and source revision. |
| **Workspace** | The assignment-specific checkout and working files used during execution, separate from other assignments' workspaces. |
| **Production-Line Coordinator** | The agent or workflow that organizes a line's work, requests assignments, and interprets outcomes according to its production methods. |
| **Plant Service** | The bookkeeping application responsible for device admission, line bindings, durable queues, claims, presence, results, and operator actions. It enforces rules rather than reasoning about the production workflow. |
| **Assignment Queue** | The durable record of accepted assignments waiting to be claimed. It need not be implemented by a separate messaging product. |
| **Claim** | A recorded ownership decision reserving an assignment for execution by a workbench under a stable claim identifier. |
| **Presence** | The service's view of a workbench's recent contact and availability. Presence alone does not establish execution readiness or prove that a process has stopped. |
| **Heartbeat** | A periodic report of continued contact or active execution. A missed heartbeat does not release a claim. |
| **Enrollment** | The process of establishing and approving the identity allowed to operate as a workbench. |
| **Registration** | Reporting machine metadata, installed versions, and capabilities under an already approved workbench identity. It does not grant admission. |

Qualify **coordinator** as production-line coordination or plant bookkeeping. Agent decisions about what work to request are separate from the service's enforcement of who may claim it.

## Verification and Outputs

| Term | Definition |
|---|---|
| **Acceptance Criteria** | The conditions an assignment or integrated change must satisfy. |
| **Verification** | Checks performed against acceptance criteria, with recorded commands, results, or assessments. An agent's success statement is not verification. |
| **Verification Evidence** | Recorded check outcomes and supporting material, such as exit codes, test results, reviewer findings, and publication read-back. |
| **Artifact** | A retained input or output associated with production, such as an assignment snapshot, log, report, or generated file. |
| **Contribution PR** | A pull request containing a worker's changes for part of the production work. |
| **Integration Branch** | The branch on which contributions are assembled and verified before integration into the product's main branch. |
| **Integration PR** | A pull request collecting contributions into an integrated change. It remains draft during assembly and verification, then becomes ready for review when acceptance criteria are met. |
| **Telemetry** | Correlated events, measurements, and traces used to understand production and infrastructure behavior. It does not replace authoritative assignment and result records. |
| **Escalation** | A request for human direction or resolution when work is blocked or exceeds the worker's authority. |

PR readiness, merge, and authorization to release a product to consumers are separate decisions. Humans authorize product releases.

## Related Specifications

- [Software factory architecture](architecture.md)
- [Workbench workloads and installation](../rfcs/001-workbench-workloads-and-installation.md)
- [Plant service](../rfcs/002-plant-service.md)
