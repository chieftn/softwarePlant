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

## Development and Improvement Rings

This is a working architecture model, captured to preserve the current reasoning and revised as we learn from actual production work. It describes five feedback rings, not five sequential stages or an implemented execution engine.

| Ring | Question | Work and outcome |
|---|---|---|
| **1. Implementation (inner loop)** | Does the implementation do the intended thing? | Write code, run focused tests, debug, and revise. Produces a working change and evidence of its behavior. |
| **2. Alignment** | Does the change conform to the product specification and engineering constraints? | Use tools and specialist agents to assess functional and non-functional requirements, architecture, contracts, security, performance, and maintainability. Produces findings and verification evidence. |
| **3. Commitment** | Are we prepared to accept and integrate the change? | Review code, challenge and resolve feedback, coordinate contribution or stacked PRs, and integrate accepted changes. Produces an accepted change and recorded disposition of review findings. |
| **4. Operations** | Is the production line functioning as intended? | Observe work queues, stalled assignments, agent/tool failures, capacity, throughput, rework, and cost. Recover or escalate under existing authority. This concerns the production line, not merely the deployed product. |
| **5. Kaizen (continuous process improvement)** | How should we improve the means of production? | Investigate recurring problems, trial changes, and evaluate outcomes. Improves agent definitions, instructions, skills, role assignments, workflows, verification, tooling, and plant capabilities under their respective ownership. |

### Relationships Between Rings

The rings can overlap and feed back into one another. Commitment review can return work to implementation or alignment. Operations runs while changes are being developed. Any ring can surface an opportunity for kaizen.

Tools, agents, and people can participate in any ring. The distinction is the question being answered and the outcome required, not which kind of actor performs the work. A ring does not require a dedicated agent, process, or service.

Alignment establishes conformance; commitment settles acceptance. Review feedback may be accepted, challenged, resolved, or escalated rather than treated as an unquestionable instruction. Evidence must apply to the revision being accepted; subsequent changes or restacking can invalidate earlier evidence. PR acceptance and integration remain distinct from human authorization to release a product.

### Operations and Kaizen Are Separate Rings

Operations sustains the current production process and responds to deviations. Kaizen deliberately changes that process. Restoring a failed tool is operational work; changing the tool, instructions, or workflow to prevent recurring failures is improvement work.

The kaizen cycle is:

**Observation → hypothesis → bounded trial → evaluation → adoption, revision, or reversal**

An improvement connects evidence of a problem to a proposed change, its expected benefit, evaluation criteria, and the decision made from the results. Evaluation considers effects on the whole workflow, not just a local speed increase.

Kaizen can use implementation, alignment, and commitment to deliver changes to the production machinery, but it remains a separate ring with its own objective. Production outcomes and improvement evidence should retain the applicable line revision so changes in results can be investigated. Changes to plant infrastructure remain subject to plant ownership; improvement work does not grant itself broader authority.

### Jidoka Across Every Ring

**Jidoka is a cross-cutting principle, not another ring or a responsibility owned only by alignment.** Build quality into the work by recognizing abnormalities, preventing defective or unverified work from silently progressing, and making intervention possible.

The common pattern is:

**Detect → contain → signal → resolve → verify before proceeding**

Each ring needs to identify abnormal conditions, how they are detected, what progression they block, who can resolve or receive an escalation, and what evidence permits work to resume. For example, failed tests prevent a readiness claim, missing verification prevents a conformance claim, unresolved blocking findings prevent integration, execution failures must not be reported as success, and unsuccessful improvement trials must not become the new standard.

Stopping means containing the affected progression, not necessarily stopping the whole factory. An expected test failure during implementation is ordinary feedback; passing that failure downstream as completed work is the problem.

### Flow and Industrial Influences

The Toyota Production System's two pillars are **jidoka** and **Just-in-Time**. Jidoka protects quality; Just-in-Time coordinates flow by producing what is needed, when needed, in the amount needed. These are principles applied across the rings, not two additional stages.

For this factory, more active agents or more generated code do not necessarily mean more useful output. Work that accumulates ahead of review and integration increases unfinished inventory and can create waiting, conflicts, and rework. Consider demand and downstream capacity rather than optimizing each ring in isolation.

Other useful Japanese concepts include **monozukuri** (making things with skill and care), **nemawashi** (consultation and groundwork before a formal decision), and **nichijo kanri** (daily management that sustains standards). **Andon** makes problems visible and requests intervention; **genchi genbutsu** emphasizes establishing facts at the source; **hansei** is deliberate reflection. These inform practices within the rings rather than supply exact alternative names for them.

The five-ring model is our working model, not an established Toyota classification. Software development involves discovery and changing requirements, so manufacturing practices must be adapted rather than copied mechanically. Ring configuration, scheduling, and runtime enforcement remain to be defined.

Study references:

- [Toyota Production System: the two pillars](https://global.toyota/en/company/vision-and-philosophy/production-system/)
- [Toyota glossary: jidoka, kaizen, nemawashi, and related concepts](https://mag.toyota.co.uk/toyota-production-system-glossary/)
- [Toyota on monozukuri](https://global.toyota/en/newsroom/corporate/39758220.html)
- [Daily management and nichijo kanri](https://artoflean.com/reference/daily-management/)
- [Lean Product and Process Development](https://www.lean.org/explore-lean/product-process-development/)

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

## Current Plant Taxonomy

The current implementation is [`src/Factory`](../src/Factory/README.md). It hosts multiple logical plants in one single-tenant Factory service. A plant is an application resource, **not** an Azure resource group, App Service instance, or Entra application.

| Concept | Current responsibility and boundary |
|---|---|
| **Factory service** | Shared web application, HTTPS API, enrollment inventory and background directory provisioning/retirement. It enforces permissions and records state; it does not reason about production work. |
| **Plant** | Named, owned collection of approved execution resources. Plant creation records its identity, owner and audit event; it does not provision Azure infrastructure. |
| **Enrollment** | First-class, service-wide device identity and lifecycle record. A device enrolls without choosing a plant and initially has no plant membership. |
| **Workbench** | Installed execution environment represented by an enrollment. Its current plant membership exposes it as a resource in that plant. |
| **Plant membership** | An enrollment's nullable plant ID and version. A device belongs to at most one plant at a time; moves/unassignment retain its identity and certificate. |
| **Registration and presence** | Machine-authenticated reports, not enrollment or approval. Factory presence works while unassigned; plant reports require current membership. |
| **Production line** | Versioned production methods, worker definitions and workload requirements. It binds to approved plant resources rather than owning the enrolled device. |
| **Workbench metaruntime** | Installed software that will configure supported MAF/Copilot executions from production-line specifications. Retooling changes execution configuration, not device identity or the Copilot SDK itself. |
| **Work assignment** | A bounded execution request with its line revision, inputs, permissions and expected outputs. This is different from assigning a device to a plant. |

```text
Factory service
    Enrollment inventory
        Device identity, credential metadata, lifecycle and Factory presence
        Current membership: no plant OR one plant, with a membership version
    Plants
        Plant A: owner and scoped access
            Currently assigned workbench resources
        Plant B: owner and scoped access
            Currently assigned workbench resources

Production-line definitions -- approved bindings --> plant execution resources
```

Enrollment is not a child of a plant. A plant's workbench record is a membership projection; the enrollment is authoritative. Former plants can retain historical rows without granting current access. One-plant device membership does not decide the still-open cardinality between production lines and plants.

### Device Lifecycle and Identity

| Operation | Identity and access result |
|---|---|
| **Enroll** | An enrollment administrator authorizes a public-certificate draft. The service provisions a dedicated Entra application/service principal and grants `Workbench.Connect`. The device proves that exact identity before activation. It starts unassigned. |
| **Assign / move / unassign** | The website changes membership using an expected version. Identity and certificate are retained. The administrator also needs ownership or plant-scoped registrar authority over the source and destination plants, where present. |
| **Retire** | Revoke Factory and plant access first, then reconcile/delete only the recorded, provisioner-owned dedicated directory objects. Offline devices and previously issued tokens do not retain admission. Retirement is terminal, not unassignment. |
| **MSI 0.1.4 install / upgrade** | Guided setup continues through enrollment and service-account authentication. Upgrade retains identity, certificate, assignment, policy/configuration and recovery evidence; an intentionally stopped service stays stopped. |
| **MSI 0.1.4 uninstall** | Stop/remove the service, attempt bounded device-self-retirement, then remove owned local credentials, configuration, journals and software. Remote failure does not block otherwise safe local cleanup. Upgrade-internal removal never retires the device. |
| **Reinstall after full removal** | Create a new installation/enrollment and credential, initially unassigned. Unconfirmed remote retirement requires administrator follow-up; reinstall does not repair that old enrollment. |

Keep the identifiers separate: `installationId` persists across software maintenance; `enrollmentId` identifies one enrollment lifecycle; `workbenchId` identifies the installation's workbench address; Entra application client ID, application object ID and service-principal object ID identify different directory objects. Machine names are display metadata, not identity keys.

The runtime reads current membership from Factory instead of treating a locally cached plant ID as authority. Plant requests carry the membership version; their stored reports are tied to the enrollment generation and version, so an old report cannot become valid by moving the device back to a former plant. Membership changes and retirement cannot stop an offline process or undo a plant request already authorized in flight.

Provisioning and retirement are durable background operations, not request-scoped tasks. Directory creates are checkpointed and uncertain outcomes reconciled rather than blindly repeated. Provisioning failures can require `needsAttention`; retirement failures can require `retirementNeedsAttention` while access remains revoked. Disabling new enrollment does not disable existing connections or retirement; retain the provisioner configuration for directory cleanup.

### Current Storage Boundaries

Cosmos stores independent documents in database `plant`, container `records`, partitioned by `/scopeId`.

| Scope | Current records |
|---|---|
| `plant:<plantId>:settings` | Plant, owner, scoped access, operation receipts and audit. |
| `enrollment:<enrollmentId>` | Enrollment/identity metadata, current membership/version, provisioning/retirement checkpoints, Factory presence, receipts and audit. |
| `installation:<installationId>` | Enrollment reservation preventing concurrent identities for the same installation. A retired enrollment permits a new lifecycle. |
| `plant:<plantId>:workbench:<workbenchId>` | Membership projection, plant registration/presence and their receipts. |

Transactions and ETag conditions protect changes within a partition. Directory effects and cross-partition membership projections are not one Cosmos transaction; they require reconciliation. The shared container is not a whole-plant document or a separate database per plant.

## Azure Logical Structure

The deployed reference environment below is current as of November 13, 2026. It is the shared control plane for the logical plants, not a deployment per plant. Workbench execution remains on the installed Windows clients.

```text
Entra tenant
    Factory API/web and public-installer registrations
    Data/provisioner managed-identity service principals
    Dedicated application/service principal for each provisioned enrollment

Azure subscription
    factory-prod resource group
        West Central US
            Linux App Service + B1 plan: shared Factory web/API/background worker
            Application VNet
                app-service subnet: App Service outbound integration
                private-endpoints subnet: Blob and Key Vault endpoints
            Private Blob storage: ASP.NET Data Protection key ring
            Private Key Vault: website credential and key-ring wrapping key
        West US 2
            Existing private Cosmos DB: plant / records
            Cosmos VNet and private endpoint
        Separate data-access and enrollment-provisioner managed identities
        Private DNS zones and two-way application/Cosmos VNet peering

Browser / installer / workbench -- outbound HTTPS --> public Factory endpoint
Factory -- selected managed identity + private network --> data services
Factory -- provisioning identity --> Microsoft Graph
```

| Deployed resource | Current name / purpose |
|---|---|
| App Service | `factory-prod-qm3qqqsg45-web`; `https://factory-prod-qm3qqqsg45-web.azurewebsites.net`. One .NET 10 deployment serves HTML, bearer API and background enrollment processing. |
| App Service plan | `factory-prod-qm3qqqsg45-plan`, Linux B1 in West Central US. This is not a validated highly available production topology; higher tiers/scale-out require separate approval and acceptance. |
| Cosmos account | `factory-prod-qm3qqqsg45-cosmos`, West US 2; strong consistency, continuous backup, autoscale throughput and Entra data authentication. Public access and local/key authentication are disabled. |
| Application VNet | `factoryw-prod-z7av642fzv-vnet`, West Central US. App Service integrates with its `app-service` subnet. |
| Cosmos VNet | `factory-prod-qm3qqqsg45-vnet`, West US 2. Two-way peering and private DNS give the application access to the existing Cosmos private endpoint. This is connectivity, not a second database region or disaster recovery. |
| Website Blob account | `stojq4jibbz5tc2web`; private `data-protection` container, `keys.xml` key ring. Assignment/evidence Blob storage remains a future capability, not a use of this container. |
| Website Key Vault | `kvojq4jibbz5tc2web`; `website-client-secret` and `cookie-protection` wrapping key. Workbench private keys are not stored here. |
| Private DNS | `privatelink.documents.azure.com`, `privatelink.blob.core.windows.net`, and `privatelink.vaultcore.azure.net`, linked to the required VNets. |

Subscription: `ca0f0469-707d-482c-9a7b-a59fc9530d7a`. Entra tenant: `b1541281-df2f-4e68-9340-14b9554fbd50`. These deployment identifiers are not plant IDs or authorization grants.

Of the Factory application and its data services, only the Factory endpoint is public. Clients receive no Cosmos, Blob or Key Vault credentials. Public data access is disabled; App Service uses VNet integration, private endpoints/DNS and route-all for the private Key Vault path. The vault retains its trusted Azure-services exception for ARM provisioning; this is not general public data access. FTP and SCM basic publishing credentials are disabled; code deployment uses Entra-authenticated Azure tooling.

Application-managed OpenID Connect handles browser sign-in; App Service Easy Auth is disabled. Browser-cookie Data Protection keys persist in Blob and are wrapped by Key Vault, so restarting the app does not intentionally invalidate all sessions. The web credential is a Key Vault reference, not a secret embedded in deployment files or installer payloads.

Use the [infrastructure inventory](../src/Factory/deploy/README.md) and its Bicep entry points for resource changes. The Cosmos, website-support, hosting and enrollment-identity templates have distinct responsibilities. Reapplying hosting must retain browser settings, both attached identities and the explicit data-identity selection; creating a logical plant requires none of these deployments.

## Microsoft Entra Design

Keep three authorization planes separate:

1. **Entra authentication and service entry:** who the caller is and whether it may enter Factory as a human or daemon.
2. **Factory authorization:** which plant/resources the caller may read or administer, and which enrollment/membership permits a machine report.
3. **Azure/Graph permissions:** what the server's selected identity may do to Azure data services or directory objects.

Plant ownership is neither Azure subscription ownership nor an Entra directory-administrator role. A production worker's role, such as Coder or Foreman, is also not an Entra permission.

### Registrations and Service Principals

| Application | Registration and credential | Permission relationship |
|---|---|---|
| **FactoryPlant web/API** | Single-tenant client ID `5b35c3b3-6112-4fbe-afa3-c0452e7a50a1`. Confidential browser client with its server credential in Key Vault; also the resource API. | Exposes delegated `Plant.Access` and the custom application roles below. Its tenant enterprise application holds human and daemon role assignments. The browser credential is not the Cosmos or Graph credential. |
| **Public workbench installer** | Separate single-tenant public client `c15ffcc4-7d09-4f5d-864f-9fcdec452f4e`; system-browser sign-in with loopback `http://localhost`. No embedded secret. | Requests only Factory's delegated `Plant.Access`; preauthorized on that API. It does not receive Graph provisioning or Azure data permissions. |
| **Enrolled workbench** | A dedicated certificate-backed application and service principal per enrollment. Client/object IDs are created and recorded during provisioning, not copied from the human or installer. | Application-only `Workbench.Connect` on Factory. The service additionally matches tenant, client ID and service-principal object ID to the enrollment; the role alone is insufficient. |

For FactoryPlant, application object ID is `ca072baf-6182-42e0-bd0a-845623e7eb94`; enterprise-application/service-principal object ID is `279d4821-2653-4b26-b834-d20c7dadbd9f`. Permission definitions belong to the registration; assignments target the resource service principal. Client ID, application object ID and service-principal object ID are not interchangeable.

The data and provisioning identities below are Azure user-assigned managed identities with Entra service principals, not public/confidential client registrations that operators create secrets for. Both are explicitly attached to App Service.

### Custom API Permissions

| Permission | Kind / allowed caller | Meaning and limit |
|---|---|---|
| `Plant.Access` | Delegated OAuth scope; human API client | Call Factory as the signed-in user. Requires `Factory.User` as well; grants no plant membership or ownership. Full requested scope is `api://<api-client-id>/Plant.Access`. |
| `Factory.User` | App role, `User` members | Human service entry for browser/API. Current plant/resource authorization is checked separately. |
| `Factory.PlantCreator` | App role, `User` members | With human service entry, create a plant and become its initial owner. Does not grant access to existing unrelated plants. |
| `Factory.EnrollmentAdministrator` | App role, `User` members | With human service entry, inspect service-wide enrollments, authorize enrollment, change membership and retire devices. Assignment also checks source/destination plant authority. Never assigned implicitly to all users or plant owners. |
| `Workbench.Connect` | App role, `Application` members | Machine service entry. Requires matching provisioned identity and current enrollment; plant reports also require current plant membership/version. No human enrollment or plant administration. |

These are custom permissions on Factory, **not** Microsoft Graph permissions or Entra directory roles. OAuth preauthorization removes a consent step for the installer; it does not assign a user role or authorize a plant operation. The existing production enterprise application's assignment-required flag remains false; Factory still enforces the required roles on every protected interface. Do not equate that flag with application authorization.

`Line.Coordinator` and line/workload grants remain proposed, not deployed API permissions.

Role/scope GUIDs are deployment-owned identifiers, distinct from these permission values. The [tenant bootstrap](../src/Factory/bootstrap/tenant/README.md) preserves existing IDs and callbacks; the narrow enrollment bootstrap adds the administrator role without replacing the API registration or assigning it broadly.

Plant ownership and scoped roles are Cosmos records, not tenant-wide Entra roles:

| Factory authority | Current behavior |
|---|---|
| **Owner** | Recorded tenant/user identity for a plant; can read it, update its editable properties and authorize workbench membership. Creating a plant establishes this ownership. |
| `Workbench.Registrar` | At plant scope, permits workbench membership changes when combined with enrollment administration. A resource-only grant does not authorize admission to the parent plant. It grants no Entra administration. |
| `Plant.Reader` / `Plant.Operator` | Recognized for scoped plant/workbench visibility. Resource-scoped access does not expose siblings. The broader execution controls described in the RFC are not implemented. |
| `Plant.EnrollmentRequester` (legacy) | Recognized only for plant-level visibility, not workbench visibility or enrollment authority. It is not an Entra app role or an alternative to `Factory.EnrollmentAdministrator`. |

There is no tenant-wide `Plant.Owner` claim. Ownership transfer and general access-management UI/API remain future work; an access record type is not evidence that all proposed operator actions exist.

### Azure Data Identity and Directory Provisioner

| Identity | Entra client ID | Granted authority |
|---|---|---|
| `factory-prod-qm3qqqsg45-data` | `a432afc9-e0ea-4168-b112-cf8e518d1b3e` | Cosmos DB Built-in Data Contributor scoped to database `plant`; Storage Blob Data Contributor at the `data-protection` container; Key Vault Secrets User at the website secret; Key Vault Crypto User at the wrapping key. Explicitly selected for data access and App Service Key Vault references. No Graph provisioning grants. |
| `factory-prod-qm3qqqsg45-enroll` | `985e52b1-364c-466c-8ef9-5edd00f12d4e` | Administrator-consented Microsoft Graph application permissions listed below. No Cosmos/Blob/Key Vault data roles. Selected only for directory provisioning and retirement. |

The data identity's principal/object ID is `43fc1703-a1d1-4f9a-ab76-81ab2d041050`; the provisioner's is `0489939c-b6c1-4325-af75-dfd2d6f274ee`. Azure RBAC and Graph app-role assignments target these principals, not their client IDs.

| Provisioner Graph application permission | Purpose and limit |
|---|---|
| `Application.ReadWrite.OwnedBy` | Create/manage dedicated machine applications and service principals owned by the provisioner. Lifecycle operations verify recorded object IDs, matching client IDs and ownership; shared API/installer/provisioner identities are protected. |
| `Application.Read.All` | Read directory metadata required to identify the API, its role and provisioning/reconciliation targets. |
| `AppRoleAssignment.ReadWrite.All` | Grant the configured API's `Workbench.Connect` role to the dedicated machine principal. **The underlying grant is tenant-wide**, even though application code accepts no arbitrary target API or role. |

Graph application permissions are assignments to the provisioner's service principal against the Microsoft Graph resource, not permissions copied into a workbench's token. Granting them requires explicit tenant approval and a directory role authorized to grant Microsoft Graph application permissions; Azure subscription Owner or Application Administrator alone is not sufficient. The approved bootstrap uses a qualified administrator, such as Privileged Role Administrator, without assigning that directory role to the service, installer or workbench.

The two managed identities improve permission selection and audit but share the App Service process compromise boundary. They are not isolated privilege domains. The broad Graph grant remains an explicit accepted risk; a separately isolated provisioner is not currently deployed.

### Authentication Flows and Credential Boundaries

1. **Browser:** Factory requests `openid` and `profile` from the configured tenant for authorization-code sign-in with PKCE, using `/signin-oidc` and `/signout-callback-oidc` at the production origin. The server validates the identity and `Factory.User`, then issues a secure HTTP-only, non-sliding one-hour cookie. Mutating forms require antiforgery protection. No API tokens are saved in browser storage; browser actions call the same application operations as the API, not loopback HTTP.
2. **Installer:** the separate public client signs in a human, obtains a delegated API token and checks enrollment-administrator authority before creating local enrollment state. Setup creates a non-exportable machine RSA/CNG key and submits only the public certificate with proof of key possession. The user's token authorizes Factory operations; it is not forwarded to Graph for provisioning.
3. **Provisioning:** the server uses its dedicated managed identity to create/checkpoint the workbench application, service principal, public credential and `Workbench.Connect` assignment. It records the machine identity independently of the authorizing human.
4. **Machine:** the dedicated Windows service account uses the local certificate/private key for client-credential token acquisition and renewal. It requests `api://<api-client-id>/.default`; Factory validates a v2 token with the configured tenant issuer and the API's client-ID audience, then checks the exact enrollment identity/state. An unassigned machine can activate and report Factory presence but cannot report to a plant.

Delegated API calls require `Plant.Access` plus the appropriate human roles; application calls require `Workbench.Connect` without delegated user scope. Tokens are checked for issuer, audience, signature, lifetime and validated tenant/object/client identity. Cookies do not authenticate `/v1` calls, and API authentication failures return errors rather than browser redirects.

Unassigning/moving does not rotate credentials. Retirement denies subsequent calls even with an unexpired machine token; directory deletion follows through the tracked retirement operation. Entra token renewal is implemented, but automatic machine-certificate rotation and expired-key recovery are not. Existing browser sessions must be renewed after role changes; plant authorization is read from current service records rather than copied permanently into the cookie.

GitHub repository access and Copilot model/runtime authentication remain separate from Factory enrollment. Neither `Plant.Access` nor `Workbench.Connect` grants either capability. Their unattended, service-owned WSL credential path remains to be implemented/accepted.

### Implemented Versus Planned

The current Factory deployment implements plants/ownership and scoped reads, enrollment inventory, certificate-backed identity provisioning, versioned membership, registration/presence, administrator retirement and device-self-retirement. The matching `0.1.4` service was deployed on November 14, 2026; health, browser authorization-code/PKCE challenge and bearer protection of the new route were verified. MSI `0.1.4` is built but native lifecycle acceptance remains pending. The earlier MSI `0.1.3` retained local identity on uninstall; it is superseded, not the revised lifecycle candidate.

Production-line execution, queues/claims/results, service-owned WSL, workload retooling, automatic certificate rotation, production signing, monitoring integration and restore/HA acceptance are not delivered by this connection slice. The new machine installation, enrollment, reboot and native removal flow still require tester-operated acceptance; deployed code and static MSI inspection are not evidence that those steps passed.

Current operational references are the [Factory implementation](../src/Factory/README.md), [API contract](../src/Factory/api/README.md), [tenant bootstrap](../src/Factory/bootstrap/tenant/README.md), [Azure inventory](../src/Factory/deploy/README.md), and [installation acceptance document](workbench-installation-test.md). MSI `0.1.4` implements the [RFC 001 lifecycle contract](../rfcs/001-workbench-workloads-and-installation.md#agreed-install-upgrade-and-uninstall-contract), automatic MSI logging, protected setup traces and diagnostic export. Build/static checks do not establish native lifecycle acceptance. The matching self-retirement service is now deployed without new Graph or device-administrator grants; actual device retirement/directory deletion remains untested.

## Earlier Execution Slice (Foundry)

This is the earlier execution foundation in `src/Workbench`, not the control plane behind the current `src/Factory` deployment. Its coordinator connection and deployment instructions do not replace the Factory API, enrollment or permissions described above.

The [plant model](plant-model.md) defines the selected approach: a **.NET Workbench Host** on each VM-based workbench, using **Microsoft Agent Framework** and its **GitHub Copilot SDK integration** to run coding workers.

The coordinator delivers assignments and tracks execution. Hosts connect outbound, prepare separate workspaces, configure worker instructions and permissions, and report results. MAF supplies the agent abstraction and broader workflow facilities; Copilot owns each coding worker's reasoning/tool loop.

The initial [workbench implementation](../src/Workbench/README.md) supplies a Foundry-hosted central coordinator foundation, an outbound Windows daemon connection, a WSL MAF/Copilot worker, assignment contracts, and publishing/deployment scripts. Foundry Invocations handles the coordinator protocol; its durable state store holds machine registrations and assignment ownership. The objective is two devboxes producing independent PRs in `chieftn/productionOutput1`. Deployed verification, production workflows, and daemon service installation remain open.