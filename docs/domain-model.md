# Software Factory Domain Model

Working document for the software factory's domain language. These concepts describe ownership, production, and output; they do not yet define architecture components.

## Terminology

| Term | Meaning |
|---|---|
| **Software Factory** | The overall software production operation, encompassing engineering ownership, production lines, and execution infrastructure. |
| **Plant** | A collection of processing infrastructure supplying the capabilities and resources needed to execute production lines. |
| **Production Line** | The jointly owned guidance, skills, worker definitions, role assignments, workflows, and verification requirements used to manufacture and evolve a product. A production line binds to a plant for execution. |
| **Bill of Materials (BOM)** | The entry point for production, specifying the inputs and scope of the work. |
| **Production Methods** | The production line's injected methods for performing the work, separate from the BOM. Includes guidance, skills, role instructions, coordination rules, and verification practices. |
| **Work Unit** | A bounded piece of work decomposed from a BOM, such as a task or bug. |
| **Worker** | An agent that performs production work in an assigned role. |
| **Occupation** | The kind of production work a worker practices: Coder, Task Coordinator (Foreman), or Quality Assurance Reviewer. |
| **Specialization** | A line-specific focus within an occupation, configured through production methods. |
| **Role** | The responsibilities assigned to a worker within a production line. |
| **Contribution PR** | A pull request containing a worker's changes for part of the production work. |
| **Integration PR** | A pull request that collects contributions into an integrated change. It remains draft during assembly and verification and becomes ready for review when the integrated work meets acceptance criteria. |
| **Product** | The enduring software offering produced and evolved by a production line. |
| **Product Release** | A specific version of a product released to consumers under human authorization. |

Use **production line**, not **product line**, for the means of production. A product line commonly means a family of related products.

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
- Architecture components and their responsibilities will be identified after the domain language is established.
