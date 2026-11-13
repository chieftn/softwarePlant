# Project Scope

This project builds the **software factory framework**: the capabilities for defining production lines, binding them to plants, and coordinating production work.

**genUI** is the pathfinding product used to exercise that framework through a concrete production-line definition. It is a generative UI library that enables LLM orchestrators to prescribe custom views and exchange information through data transforms expressed as LLM-generated JavaScript. genUI's product behavior is separate from the factory's production machinery; its consuming orchestrators are not the factory's workers.

# Software Factory Model

A software factory is an AI-native software production operation. Engineers jointly own and evolve a **production line**: the guidance, skills, worker definitions, role assignments, workflows, and verification requirements used to manufacture and evolve a **product**.

Agents are **workers** that perform production work in assigned **roles**. A role defines a worker's responsibilities within the production line.

The production line binds to a **plant**, a collection of processing infrastructure supplying the resources and capabilities needed to execute the work. The line defines how the product is made; the plant supplies the execution infrastructure. Infrastructure-sharing cardinality remains unspecified.

The product is the enduring software offering. **Product releases** are specific versions made available to consumers under human authorization. Routine manufacturing work is autonomous; humans set direction, resolve escalations, and authorize release.

See the [glossary](glossary.md) for term definitions.

## Relationships and Ownership

- The software engineers contributing to a product jointly own and evolve its production line.
- A production line defines how the product is made.
- A BOM supplies production inputs and scope; production methods supply the line's methods through a separate injection point.
- A BOM is decomposed into work units assigned to workers.
- A production line defines specialized agents by occupation, with guidance and skills supplied through production methods.
- Workers perform production work according to their assigned roles.
- A plant supplies the infrastructure on which that production work executes.
- A production line produces and evolves a product.
- Product releases identify successive versions of the product.
- Humans authorize release to consumers. Routine manufacturing work is autonomous, with human involvement for exceptions and direction.

## Worker Occupations

| Occupation | Responsibilities | Outputs |
|---|---|---|
| **Coder** | Implements assigned work units, adds relevant tests, and addresses review findings. | Contribution PRs and supporting verification evidence. |
| **Task Coordinator (Foreman)** | Decomposes production work, coordinates assignments and dependencies, tracks progress, coordinates contribution integration, and escalates blocked work. | Work-unit assignments, integration progress, and escalations. |
| **Quality Assurance Reviewer** | Reviews contributions and integrated changes against acceptance criteria and the concerns of its specialization. | Findings and an explicit assessment within its review scope. |

Occupation describes the kind of work; specialization describes its focus; role describes the responsibilities assigned for particular production work. For example, a Quality Assurance Reviewer can specialize in security or performance.

Occupations and specializations do not require separate agent runtime implementations. The production line supplies their methods and review requirements through production methods.

## Production Flow

**BOM → work decomposition → worker assignments → contribution PRs → integrated draft PR → ready-for-review PR**

A BOM can produce multiple contribution PRs. Workers coordinate the integration of their changes into an integration branch, with contribution PRs targeting that branch. The integration PR targets the product's main branch.

Workers merge branches or commits; PRs track and coordinate those changes. The integration PR moves from draft to ready for review rather than requiring a new PR.

Production methods specify how decomposition, worker coordination, integration, and verification are performed. These methods are injected into the production line rather than embedded in each BOM or fixed as one universal factory workflow.

PR readiness is distinct from human authorization to release the product to consumers.

## Definition Versus Execution

The production line's definition is distinct from the infrastructure executing it. Guidance, skills, worker definitions, role assignments, and workflows belong to the line; the plant supplies execution resources.

This separation is intentional. In physical manufacturing, a production line commonly includes installed equipment as well as the production process. Here, the line definition and its execution infrastructure are separate concepts.

**Plant** and **factory** do not distinguish continuous from discrete production. Product releases are discrete outputs, even when the production line operates continuously.

## Production-Line Metadocument

A production-line definition is a versioned metadocument describing the product's business problem and the line's production methods: guidance, worker definitions, skills, and tools. The initial [JSON Schema](../schemas/production-line.schema.json) describes this document, and the [genUI example](../examples/genui/production-line.json) provides the pathfinding instance.

`schemaVersion` identifies the document format; `version` identifies the revision of the line definition. Neither is the product's release version.

The definition is independent of plant infrastructure. Plant binding and BOMs are separate concerns and are not embedded in this initial schema. Tool entries describe required capabilities, not implementations, credentials, or permission grants. Skill instructions and worker roles are declarative inputs, not an implemented execution engine.

Worker, skill, and tool IDs are unique within their respective collections. Each worker's skill and tool references resolve to entries in the same definition. These are semantic constraints; the JSON Schema validates structure but does not enforce ID uniqueness or reference resolution.

## Open Decisions

- The cardinality of the relationship between production lines and plants remains unspecified. Neither dedicated infrastructure nor shared infrastructure is assumed.
- The exact contents of a BOM remain unspecified.
- The initial metadocument represents production methods inline; their runtime injection mechanism remains unspecified.
- The remaining architecture components and their responsibilities require definition.

# Architecture

The architecture maps the software factory model to software components, their responsibilities, and their interactions. Domain concepts guide this work; they do not imply one component or service per concept.

The mapping establishes:

- How engineers define, share, and evolve a production line's guidance, skills, worker definitions, role assignments, workflows, and verification requirements.
- How a plant supplies execution capabilities and how a production line binds to those capabilities.
- How production work is executed and its outputs associated with the product and product release.
- How verification results and production telemetry feed corrective work, and when the factory escalates to engineers.
- How human release authorization controls exposure of the product to consumers.
- How factory components interact with the external systems shown in the system context.

Component boundaries follow these responsibilities and interactions. The mapping preserves the separation between production-line definition and plant infrastructure without assuming infrastructure-sharing cardinality.

## System Context

![Software Factory system context diagram](software-factory-context.png)

## Initial Plant Execution Slice

The [plant model](plant-model.md) defines the selected approach: a **.NET Workbench Host** on each VM-based workbench, using **Microsoft Agent Framework** and its **GitHub Copilot SDK integration** to run coding workers.

The coordinator delivers assignments and tracks execution. Hosts connect outbound, prepare separate workspaces, configure worker instructions and permissions, and report results. MAF supplies the agent abstraction and broader workflow facilities; Copilot owns each coding worker's reasoning/tool loop.

The initial [workbench implementation](../src/Workbench/README.md) supplies a Foundry-hosted central coordinator foundation, an outbound Windows daemon connection, a WSL MAF/Copilot worker, assignment contracts, and publishing/deployment scripts. Foundry Invocations handles the coordinator protocol; its durable state store holds machine registrations and assignment ownership. The objective is two devboxes producing independent PRs in `chieftn/productionOutput1`. Deployed verification, production workflows, and daemon service installation remain open.