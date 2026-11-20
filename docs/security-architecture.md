# Security architecture: permissions and grants

As of November 17, 2026. This describes the current repository sources, including the managed-execution increment now deployed and packaged, and separately identifies recorded deployment and tester reports. It is not a vulnerability audit, a live access inventory, or authorization to change anything.

**Decision: “let's leave things as is.”** Keep the existing implementation, including local Start/Stop line and assignment-scoped consent. The alternatives discussed below are not implemented or approved for rollout.

## 1. What is running, and what is only local

| Area | Evidence and status |
|---|---|
| Factory service | Deployed to Azure App Service as `cfca26c6-d010-41a7-8d52-9f9e5ff4f279`, RuntimeSuccessful, including line-managed settings and the existing queue/merge gates. Postflight settings, authentication, both managed identities, networking and vault grants matched preflight. **Execution remains off.** See [recorded release](development-missions-context.md#completed-release---november-17-2026). Anonymous health/auth challenge checks are not authenticated workflow acceptance or a complete live grant inventory. |
| Windows workbench | MSI `0.1.9`, release `workbench-0.1.9-managed-001`, is built and statically verified, not installed by the assistant. Previous-client installation and SDK preparation were tester-reported; they do not prove this replacement package or a model workflow. See [current installation handoff](workbench-installation-test.md#managed-execution-settings---current-candidate-019). |
| Managed line execution and local buttons | Optional line execution settings are deployed server-side; assignment-scoped local consent, Start/Stop line, policy resolution and execution-state reporting are packaged in `0.1.9`, not `0.1.8`. No execution setting, line configuration or local consent was enabled by the rollout. See [installation status](workbench-installation-test.md). |
| Acceptance | SDK startup/shutdown is not proof of Copilot entitlement, model access, token renewal during real work, project verification, or a completed mission. Real model/workflow acceptance remains outstanding. The readiness implementation explicitly emits `modelAccessVerified = false`: [WorkerReadiness.cs](../src/Factory/Factory.Workbench.Worker/WorkerReadiness.cs#L10-L46). |

The current Factory implementation is under `src\Factory`. Older `src\Workbench` code is a separate Foundry-based implementation, not another entry point into Factory enrollment. Section 9 distinguishes it. Conceptual roles, tools and skills in architecture/RFC/example documents do not themselves grant Entra, GitHub, Azure or Windows permissions. Source enforcement below takes precedence over older prose describing unimplemented workers or superseded assignments.

## 2. Who can grant what

There are several independent approvals, not one “Factory login” that grants everything:

```text
Tenant administrator ── Entra user roles ──> Factory user / enrollment administrator
Tenant administrator ── Graph grants ─────> dedicated directory provisioner
Azure administrator ─── data-plane RBAC ──> separate service data identity

Plant owner ─────────── line binding ─────> every active workbench in that plant
Enrollment administrator + plant authority ── membership ──> one enrolled machine
GitHub App owner / installation administrator ── selected repositories ──> service
Line owner's GitHub account ── separate Copilot OAuth authorization ─────> service
Local Windows administrator ── Start line consent and local limits ─────> workbench

Service ── checked mission + temporary credentials ──> Windows host ── pipe ──> Linux worker
Human / GitHub policy ── review and merge ──> externally observed PR completion
```

The service selects/claims tasks and verifies repository authority, publication and review/merge receipts. The worker runs repository-defined Implementation/Alignment, project commands and Git operations; the host supplies local enforcement and service communication. These responsibilities are concrete in [EnrollmentMissions.cs](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L45-L195), [WorkbenchDevelopmentExecution.cs](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L15-L126), and [worker wiring](../src/Factory/Factory.Workbench.Worker/Program.cs#L108-L142).

### Factory people and devices

| Principal / grantor | Grant and enforcement | Important limit |
|---|---|---|
| Tenant administrator → human user | Bootstrap defines `Factory.User`, `Factory.PlantCreator`, `Factory.EnrollmentAdministrator`, and application-only `Workbench.Connect`; selected user assignments are made by privileged bootstrap. [Role definitions](../src/Factory/bootstrap/Common.ps1#L109-L139), [tenant assignments](../src/Factory/bootstrap/tenant/Initialize-Tenant.ps1#L93-L119), [enrollment administrator assignment](../src/Factory/bootstrap/tenant/Initialize-WorkbenchEnrollment.ps1#L93-L105). | Roles permit service entry or particular operations; they do not supply repository/model credentials or override resource ownership. |
| Browser user | Single-tenant Entra authorization-code sign-in with PKCE; secure, HttpOnly cookie; antiforgery validation for forms. Cookie lifetime is one hour, without sliding expiry; OIDC tokens are not saved in the session. [WebsiteAuthentication.cs](../src/Factory/Factory.Plant.Service/Web/WebsiteAuthentication.cs#L24-L144). | Browser cookies do not authenticate `/v1`: it selects JWT bearer authentication. Cookie validation checks embedded identity/roles, not a fresh directory-role lookup. Removing an Entra assignment is not instantaneous invalidation of an existing cookie. [Scheme and principal validation](../src/Factory/Factory.Plant.Service/Web/WebsiteAuthentication.cs#L57-L75), [caller validation](../src/Factory/Factory.Plant.Service/Web/WebsiteAuthentication.cs#L171-L180). |
| Human API caller | JWT validation requires configured tenant, issuer/audience, expiry, RSA signature, v2 token and single `tid`, `oid`, `azp` identities. Human policies additionally require delegated `Plant.Access` and `Factory.User`; creation requires `Factory.PlantCreator`. [ApiAuthentication.cs](../src/Factory/Factory.Plant.Service/ApiAuthentication.cs#L12-L123). | Token role claims are not a live directory check. Identity is tenant/object ID, not display name or email. |
| Plant creator / owner | Creation records the caller as owner. Owner can update the plant. Stored access records support `Plant.Reader`, `Plant.Operator`, `Workbench.Registrar` and `Plant.EnrollmentRequester` in the specific read/registrar checks. [PlantOperations.cs](../src/Factory/Factory.Plant.Service/PlantOperations.cs#L16-L22), [ownership creation/update](../src/Factory/Factory.Plant.Service/PlantOperations.cs#L75-L108), [scoped access checks](../src/Factory/Factory.Plant.Service/PlantOperations.cs#L319-L362). | These stored roles are not equivalent to unrestricted administration. The current [plant routes](../src/Factory/Factory.Plant.Service/PlantEndpoints.cs#L22-L108) do not expose a general access-record grant/revoke interface; do not assume organization/group delegation exists. |
| Production-line owner | Only the owner can read/update the line; managed source/task references are checked against that owner's connections. Binding a line requires ownership of both active plant and line. [ProductionLineOperations.cs](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L10-L17), [line writes](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L34-L99), [binding](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L135-L176), [owner check](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L229-L233). | One binding belongs to the **plant**, not an individual machine. Every active member discovers it. |
| Enrollment administrator → machine | Human must have `Factory.EnrollmentAdministrator`. Enrollment requires a protected five-minute challenge tied to caller and draft, signed by the submitted machine key. Provisioner creates the device application/principal and assigns only the configured API's `Workbench.Connect`. [EnrollmentOperations.cs](../src/Factory/Factory.Plant.Service/EnrollmentOperations.cs#L18-L101), [DirectoryProvisioner.cs](../src/Factory/Factory.Plant.Service/DirectoryProvisioner.cs#L71-L141). | Enrollment initially establishes device identity, not plant membership, local code-execution consent, GitHub access or Copilot authorization. |
| Enrollment administrator **and** source/target plant owner or plant-wide `Workbench.Registrar` → membership | Assignment checks both relevant plants, active enrollment and expected version; membership changes increment `AssignmentVersion`. [EnrollmentAssignment.cs](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L26-L66), [registrar check](../src/Factory/Factory.Plant.Service/PlantOperations.cs#L16-L22). | Enrollment-administrator role alone does not bypass plant registration authority. Moving one device does not change sibling enrollments. |
| Enrolled machine | App-only bearer policy requires `Workbench.Connect`, excludes delegated/user tokens, then operations match tenant, application client ID and service-principal object ID against the exact ready/active enrollment. Plant configuration additionally checks current membership and assignment version. [API machine policy](../src/Factory/Factory.Plant.Service/ApiAuthentication.cs#L110-L123), [RequireMachineAsync](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L9-L23), [MachineConfigurationAsync](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L182-L200). | Possessing one enrollment identity is not intended to authorize sibling devices. Machine identity is not Windows impersonation of the line owner. |

### Azure identities and actual grant scope

Infrastructure templates describe requested grants, not a complete live RBAC inventory. Azure deployment authority and tenant bootstrap authority belong to the administrator executing those operations; runtime users and devices are not given those privileges.

| Recipient | Administrator-granted authority | Scope / enforcement evidence |
|---|---|---|
| Dedicated **provisioning** identity | Microsoft Graph application permissions `Application.ReadWrite.OwnedBy`, `Application.Read.All`, `AppRoleAssignment.ReadWrite.All`. | Explicit privileged bootstrap acknowledgment is required. The role-assignment permission is **tenant-wide**; service checks limiting use to `Workbench.Connect` do not narrow the Graph grant. [Bootstrap](../src/Factory/bootstrap/tenant/Initialize-WorkbenchEnrollment.ps1#L10-L17), [exact grants and warning](../src/Factory/bootstrap/tenant/Initialize-WorkbenchEnrollment.ps1#L108-L145). |
| Same provisioning identity | Service creates certificate-backed applications it owns, verifies principal ownership, grants the configured role, and only retires recorded matching identities; shared API/installer/provisioner applications are protected from this delete path. | [DirectoryProvisioner.cs](../src/Factory/Factory.Plant.Service/DirectoryProvisioner.cs#L71-L179). The [identity Bicep](../src/Factory/deploy/enrollment-identity.production.bicep#L1-L19) merely creates the identity; it grants no Graph or Azure data permission itself. |
| Separate **data** managed identity | Cosmos DB Built-in Data Contributor (`…0002`), assigned at database `plant`. | All Factory records use container `records`, partitioned by `/scopeId`. This is a database grant, **not per-plant Azure isolation**. Cosmos key authentication is disabled. [cosmos-resources.bicep](../src/Factory/deploy/cosmos-resources.bicep#L48-L76), [assignment](../src/Factory/deploy/cosmos-resources.bicep#L107-L115). Application code enforces ownership; ETag-conditional, same-partition transactions enforce durable updates: [CosmosRecordStore.cs](../src/Factory/Factory.Plant.Service/Storage/CosmosRecordStore.cs#L23-L85). |
| Data identity | Storage Blob Data Contributor at `data-protection` container; Key Vault Crypto User at `cookie-protection` key. | Protects/persists application Data Protection keys, including cookie protection. These are separate container/key grants, not storage-account or vault administration. [website.production.bicep](../src/Factory/deploy/website.production.bicep#L65-L83), [key and grant](../src/Factory/deploy/website.production.bicep#L108-L132), [runtime persistence](../src/Factory/Factory.Plant.Service/Web/WebsiteAuthentication.cs#L145-L157). |
| Data identity | Key Vault Secrets User at `website-client-secret`. | App Service resolves the browser confidential-client credential using the selected data identity. This production browser secret is distinct from Development-only `ClientSecret` **data authentication**. [website-credential.production.bicep](../src/Factory/deploy/website-credential.production.bicep#L20-L42), [App Service identity/settings](../src/Factory/deploy/service.production.bicep#L84-L135). |
| Data identity | Custom `Factory GitHub onboarding secrets (<resource group>)`: only `Microsoft.KeyVault/vaults/secrets/getSecret/action` and `Microsoft.KeyVault/vaults/secrets/setSecret/action`. | Assignment is at the **entire website vault**. It covers every secret value there, including Copilot secrets, not only names prefixed for GitHub. It does not grant list/delete/purge, key management or RBAC management. This overlaps and is broader than the website-secret-only read grant. [github-connections.production.bicep](../src/Factory/deploy/github-connections.production.bicep#L17-L47). |
| Data identity in integrated `main.bicep` | Blob Data Contributor for each declared container; the same secret/key-specific website grants; Monitoring Metrics Publisher at Application Insights. | [Container/website grants](../src/Factory/deploy/main.bicep#L207-L277), [monitor assignment](../src/Factory/deploy/main.bicep#L357-L365). This is another template path, not evidence that every resource/grant in it exists in the recorded standalone deployment. |

Production data authentication explicitly selects the user-assigned managed identity. `AzureCli` and `ClientSecret` data paths are allowed only in Development; there is no ambient production developer-login fallback. Directory provisioning separately selects its own identity, with `ClientSecret` provisioning also Development-only. [Program.cs](../src/Factory/Factory.Plant.Service/Program.cs#L56-L80), [ApiSettings.cs](../src/Factory/Factory.Plant.Service/ApiSettings.cs#L35-L42), [EnrollmentSettings.cs](../src/Factory/Factory.Plant.Service/EnrollmentSettings.cs#L13-L24), [Graph credentials](../src/Factory/Factory.Plant.Service/DirectoryProvisioner.cs#L47-L53).

The identities are distinct grants, but both are attached to the **same App Service**; this is not process isolation from a compromised service. Public HTTPS service entry still needs application authorization. Private Cosmos, Blob and vault connectivity is additional network restriction, **not authorization**. The production website vault is RBAC-enabled with public access disabled and an Azure-services provisioning bypass; Blob also disables shared keys. App Service uses HTTPS/TLS, disables FTP/SCM basic publishing credentials and routes to its VNet. [Website network settings](../src/Factory/deploy/website.production.bicep#L29-L50), [vault](../src/Factory/deploy/website.production.bicep#L85-L105), [service](../src/Factory/deploy/service.production.bicep#L84-L155).

## 3. Repository authority: GitHub App, not desktop login

The Factory owner creates a **private, owner-scoped repository GitHub App** through Repository connections and approves installation on its owning account using **Only select repositories**. The service verifies owning account, selected installation, nonsuspended state and actual permissions. Connections belong to the Factory user's Entra identity; organization-wide Factory sharing/delegation is not implemented. [Manifest permissions](../src/Factory/Factory.Plant.Service/ManagedGitHubConnections.cs#L74-L96), [connection owner check](../src/Factory/Factory.Plant.Service/ManagedGitHubConnections.cs#L21-L40), [installation verification](../src/Factory/Factory.Plant.Service/GitHubOnboardingClient.cs#L49-L100).

| Approval | What it permits |
|---|---|
| Read-only connection | Contents, Issues and Pull requests read. |
| Publication opt-in at connection creation | Contents and Pull requests write; Issues remains read. This does not itself permit execution on a workbench. |
| Line source binding | Names source repository, base branch, workload folder and connection reference. |
| Independent line task binding | Names GitHub Issues repository, selection labels and connection reference; it can differ from source. |
| Mission source-read grant | A token narrowed to the named source repository and Contents read. |
| Mission source-write grant | Contents write only after recorded candidate publication intent, with current mission authority still valid. |
| Service publication/review operations | Service uses its own appropriate tokens, verifies remote branch/candidate and creates/reads the draft PR; it does not delegate task discovery or PR-merge verification to agent text. |

Sources: [line bindings/policy](../src/Factory/Factory.Contracts/ProductionLines.cs#L5-L12), [installation-token narrowing](../src/Factory/Factory.Plant.Service/GitHubConnections.cs#L186-L217), [task/source/publication operations](../src/Factory/Factory.Plant.Service/GitHubDevelopmentRepository.cs#L50-L114), [write/publication gate](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L300-L319).

The App private key is saved directly in the service's website Key Vault; Cosmos contains connection identity, owner, selected repositories and lifecycle state, not the private key. The service reads the key to sign an App JWT expiring nine minutes after creation. Mission installation-token requests specify one `repository_names` entry and needed permissions; the response must expire more than one minute and no more than one hour plus five minutes from issuance. The onboarding verification path also uses a read-only installation token to enumerate the installation's selected repositories. [Secret storage](../src/Factory/Factory.Plant.Service/GitHubConnectionSecrets.cs#L21-L93), [token/JWT implementation](../src/Factory/Factory.Plant.Service/GitHubConnections.cs#L186-L247), [onboarding enumeration](../src/Factory/Factory.Plant.Service/GitHubOnboardingClient.cs#L71-L100).

**A repository-scoped token is not branch-, commit- or task-scoped.** `factory/mission-<id>`, candidate checks and publication intent are Factory/Git checks, not equivalent GitHub token restrictions. GitHub branch protection and account/installation policy remain independent external controls; their live configuration was not inspected.

Workers receive temporary source credentials through the host/worker pipe, not an App private key or the desktop user's `gh` login. Git clears normal credential helpers, disables interactive prompts, ignores global/system Git config and supplies a temporary helper via command-local configuration. The token is passed to the Git/helper child in environment variables; it is not embedded in the remote URL or command arguments. The helper requires HTTPS, `github.com` and the exact repository path. This reduces routine persistence; it does **not** make process environments or same-user processes a secret-isolation boundary. [GitMissionRepository.cs](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L214-L246), [helper](../src/Factory/Factory.Workbench.Worker/Program.cs#L20-L37).

Disabling a managed connection blocks new resolution/authority checks but does not uninstall the GitHub App, erase vault keys, undo pushes/PRs or revoke already-issued tokens. Installation removal/suspension, authorization changes and key rotation are separate GitHub/operator actions; do not assume key rotation alone invalidates every token already issued. [Disable/resolve](../src/Factory/Factory.Plant.Service/ManagedGitHubConnections.cs#L174-L217).

Configuration-backed `Factory:GitHub:Connections` references remain supported alongside managed connections. They carry explicit installation, repository allowlist and publication settings; the private-key configuration must already be securely resolved. They do not gain managed per-user connection ownership merely by having a logical reference. [Legacy connection validation](../src/Factory/Factory.Plant.Service/GitHubConnections.cs#L11-L55), [reference handling](../src/Factory/Factory.Plant.Service/ManagedGitHubConnections.cs#L185-L199), [token checks](../src/Factory/Factory.Plant.Service/GitHubConnections.cs#L186-L217).

## 4. Model authority: separate Copilot account authorization

The **line owner's connected GitHub account/subscription** supplies Copilot access. It uses a different private GitHub App whose manifest requests **no repository permissions** and no repository installation. Repository installation is not Copilot consent; enrollment and Windows identity are neither of those authorizations. One Factory-owner authorization can serve that owner's assigned lines/machines, sharing the GitHub account's subscription limits and organization policies. [Owner-keyed authorization and empty permission manifest](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L18-L84), [mission uses line owner](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L255-L275).

| Lifecycle step | Enforcement and credential location |
|---|---|
| Connect account | Owner-bound, expiring protected OAuth state; PKCE S256; `offline_access`; account selection. [ManagedCopilotAccess.cs](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L102-L140). |
| Store authorization | OAuth client secret and renewable user token bundle go to attempt-specific vault secrets; Cosmos selects the app/credential IDs and holds metadata. These values do not become line settings or copied local configuration. [CopilotSecrets.cs](../src/Factory/Factory.Plant.Service/CopilotSecrets.cs#L10-L81), [record updates](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L141-L168). |
| Refresh | Before 70 minutes or less remain, a conditional Cosmos transition owns refresh, preventing simultaneous use of a rotating refresh token. Interrupted exchange/refresh requires recovery of an already saved vault result, or explicit reauthorization when no result was saved—not blind replay. [Acquire/recovery](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L141-L168), [refresh and conditional write](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L187-L249). |
| Deliver access token | `mission/model-credential` requires the exact mission ID/digest and recorded `executing` state. Membership, policy, issue, repository and model authority are checked before **and after** acquisition. Service returns only the access credential, through the enrollment's bearer API then host-owned pipe. [Mission gate](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L229-L297), [route protection](../src/Factory/Factory.Plant.Service/EnrollmentMissionEndpoints.cs#L9-L14), [pipe](../src/Factory/Factory.Workbench.Worker/WorkerPlantPipe.cs#L55-L60). |
| SDK use / renewal | Session-scoped token provider, `UseLoggedInUser=false`, frozen model and `github.com` host check. Tokens are obtained in memory, not in startup packets, command arguments or mission journals. Refresh tokens/client secrets remain service-side. [SDK setup](../src/Factory/Factory.Workbench.Worker/CopilotMissionAgent.cs#L57-L95), [host/model validation](../src/Factory/Factory.Workbench.Worker/CopilotModelAuthentication.cs#L7-L22), [noncredential startup packet](../src/Factory/Factory.Workbench/WslMissionWorker.cs#L63-L85). |
| Disable | Factory state becomes `disabled`; new credentials and continued mission authorization are denied. Already-issued GitHub user tokens are not externally revoked by that state change. [Disable/require](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L170-L185). |

These are GitHub **user/App** access tokens, not GitHub-enforced per-mission or per-model capabilities. The OAuth adapter accepts access-token lifetimes of two to eight hours and bounded refresh-token lifetimes; “temporary” does not mean immediately revoked when local consent changes. Revoke the authorized App on GitHub when external revocation is required. Successful OAuth proves account authorization, not subscription entitlement or availability of the selected model; SDK readiness deliberately makes no model call. [Token validation](../src/Factory/Factory.Plant.Service/GitHubCopilotOAuth.cs#L70-L104), [readiness](../src/Factory/Factory.Workbench.Worker/WorkerReadiness.cs#L29-L46).

Saved registration/token attempts and prior vault versions are not automatically deleted by Disable. Recovery is deliberately separate from replay: [recovery state machine](../src/Factory/Factory.Plant.Service/ManagedCopilotAccess.cs#L141-L174), [vault get/set-only implementation](../src/Factory/Factory.Plant.Service/CopilotSecrets.cs#L51-L87). There is no Azure model-provider authorization implemented by these paths.

## 5. Device authority: elevated setup, dedicated runtime, local consent

### Windows and enrollment credentials

Setup requires a local Windows administrator. It administers service configuration, protected files, machine certificates and the service-owned WSL prerequisites. Runtime instead uses a dedicated virtual account `NT SERVICE\<installation service name>`, not the interactive administrator's desktop identity. Service binaries receive read/execute access; protected configuration is administrator/SYSTEM-writable and service-readable; runtime journals and Linux environment storage are service-modifiable. [Installer entry](../src/Factory/Factory.Workbench.Installer/Program.cs#L13-L33), [service configuration](../src/Factory/Factory.Workbench.Setup/WindowsWorkbenchService.cs#L14-L29), [account naming](../src/Factory/Factory.Workbench/LocalState.cs#L15-L22), [ACLs](../src/Factory/Factory.Workbench/MachineCertificates.cs#L153-L220).

Enrollment generates a LocalMachine certificate using a nonexportable software CNG RSA-3072 signing key, valid for 180 days. Private-key ACLs permit administrator/SYSTEM control and the specific service's read access. MSAL authenticates the **device application** with that certificate for the configured Factory API scope; this is not a stored human login. Automatic bearer-token acquisition is implemented; automatic certificate renewal is not established by that mechanism. [Certificate creation](../src/Factory/Factory.Workbench/MachineCertificates.cs#L223-L255), [key ACL](../src/Factory/Factory.Workbench/MachineCertificates.cs#L340-L357), [CertificateTokenSource](../src/Factory/Factory.Workbench/WorkbenchPlantConnection.cs#L7-L28), [renewal limitation](workbench-installation-test.md#L331-L333).

Local administrators remain trusted: nonexportability and ACLs are not protection against the host administrator controlling the service. Commit-author name/email are Git metadata, not the identity authenticating to Factory, GitHub or Copilot.

### Local Start/Stop line

The local optional `ProductionLineExecution` profile contains `Enabled`, `Model`, Git author metadata, trusted `Reviewers` and `ApprovalTimeoutHours`. Legacy line JSON omits it when absent. The website edits these nonsecret settings; it does not grant local execution on its own. [ProductionLines.cs](../src/Factory/Factory.Contracts/ProductionLines.cs#L14-L44), [form mapping](../src/Factory/Factory.Plant.Service/Web/WebsiteModels.cs#L19-L56), [website controls](../src/Factory/Factory.Plant.Service/Views/ProductionLines/Edit.cshtml#L63-L74).

**Start line is local**, in elevated Setup. It:

1. Shows the actual line, repositories, model, author and reviewers and asks permission to execute project instructions/commands and publish draft PRs.
2. Refetches the assignment and resolves the matching workload. Requires enabled line execution, shell/publication policy, real verification commands, explicit `pull_request_merged` completion and the correctly prepared worker/release.
3. Refuses an active/unresolved worker journal; stops the service and rechecks under the runtime lock.
4. Saves the source/task allowlist, local `AllowShellExecution=true`, `AllowPublication=true`, and `ExecutionConsent`; restarts the service. Runtime then checks its actual readiness and service authority, rather than treating button success as a successful mission.

Evidence: [SetupForm.cs](../src/Factory/Factory.Workbench.Installer/SetupForm.cs#L261-L293), [WorkbenchExecutionSetup.cs](../src/Factory/Factory.Workbench.Setup/WorkbenchExecutionSetup.cs#L7-L67), [runtime gates](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L15-L84), [actual status](../src/Factory/Factory.Workbench.Host/WorkbenchRuntime.cs#L77-L110).

**What consent binds—and does not bind:** `WorkbenchExecutionConsent` matches `PlantId`, `AssignmentVersion`, `BindingVersion`, `ProductionLineId`, `SourceRepository` and `TaskRepository`. It does **not** bind the line revision, model/reviewer profile, connection references, base branch, workload path, source commit, issue, or exact repository/workload content. Start checks a fresh exact line revision at that moment; the persisted consent is a narrower assignment/repository match, not continuing approval of every later revision. [Consent definition](../src/Factory/Factory.Contracts/WorkbenchConfiguration.cs#L73-L91), [fresh workload identity](../src/Factory/Factory.Contracts/RepositoryWorkloads.cs#L126-L138).

`WorkbenchExecutionConfiguration.Resolve` consumes current nonsecret line settings when local consent still matches; ordinary local permission/allowlist gates remain. An **existing mission** is separately revoked when its execution profile changes (`ProductionLineExecution.Matches` compares enablement/model/author/reviewers/deadline), membership/binding/repositories/connections change, or shell/publication authority is removed. Not every line revision invalidates an existing mission: it otherwise retains frozen inputs and limits. Later new work can use revised line/profile/workload settings under still-matching consent, subject to all other gates and retained-claim reconciliation. Do not describe this as exact-configuration approval or as an automatic permission prompt for each edit. [Resolve](../src/Factory/Factory.Workbench/WorkbenchExecutionConfiguration.cs#L5-L24), [profile equality](../src/Factory/Factory.Contracts/ProductionLines.cs#L30-L33), [host match](../src/Factory/Factory.Workbench/WslMissionWorker.cs#L98-L111), [service match](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L229-L252).

**Stop line** clears `ExecutionConsent`, disables local Development execution, stops the local Windows service to reconcile its worker, and can restart service connectivity when previously requested. It does not erase enrollment, repository allowlist or the stored shell/publication booleans. An unresolved journal causes an error **after consent is revoked** and is retained for reconciliation. This differs from simply stopping the Windows service without revoking consent. [Stop implementation](../src/Factory/Factory.Workbench.Setup/WorkbenchExecutionSetup.cs#L69-L88).

The local service policy `Factory:Development:Enabled` is now nullable:

| Value | Current local-source meaning |
|---|---|
| Explicit `false` | Global operator shutdown, including line-managed execution. |
| Unset | Only an explicitly enabled managed line can supply execution policy. Legacy lines remain disabled. |
| Explicit `true` | Legacy execution can use service reviewers/deadline; a managed line still supplies its own enabled profile. |

An enabled profile is still not local consent or a repository/model grant. Legacy local Development configuration remains readable where neither line profile nor consent exists. **Do not silently remove or reinterpret a production shutdown setting during deployment or activation.** [Service resolution](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L8-L41), [legacy local resolution](../src/Factory/Factory.Workbench/WorkbenchExecutionConfiguration.cs#L7-L23).

## 6. Mission permission checks and completion

| Boundary | What is checked |
|---|---|
| Before claim | Current exact assignment/line revision, service enable policy, line shell/publication approval, line-owner repository connections/publication access, owner model authorization; host also requires local allowlists/permissions and service-identity worker readiness. [Service claim](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L45-L62), [host](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L24-L84). |
| Eligible task | Open issue, not PR, in the task repository; every required selection/approval label and explicit bullet-list acceptance criteria. Labels approve eligibility, not arbitrary future edits. [GitHubDevelopmentRepository.cs](../src/Factory/Factory.Plant.Service/GitHubDevelopmentRepository.cs#L50-L77). |
| Frozen scope | Workload contains exact configuration, source commit, manifest/schema and both role instruction files with a digest; mission additionally freezes issue title/body/criteria/labels/time, model and deadline in its digest. Specification files in the checkout come from the pinned source commit; the workload digest is not a claim that every spec file was separately copied into the four-file payload. [RepositoryWorkloads.cs](../src/Factory/Factory.Contracts/RepositoryWorkloads.cs#L140-L179), [DevelopmentMissions.cs](../src/Factory/Factory.Contracts/DevelopmentMissions.cs#L8-L55), [pinned checkout verification](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L96-L125). |
| Durable ownership | Enrollment slot and task-repository/issue claim are durably reserved using conditional writes. Partial reservation is reconciled; another worker cannot simply reclaim an uncertain task. [EnrollmentMissions.cs](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L115-L208). |
| Continued authority | Current enrollment/membership, binding, repository/connection and execution-profile checks; frozen issue title/body/update-time changes block execution. Issue-open checks are deliberately skipped during read-only PR review/merge waiting so issue auto-close does not defeat completion observation. [EnrollmentMissions.cs](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L229-L297). |
| Worker loop | Implementation → commit → independently run declared project verification → read-only Alignment. Checks must match the candidate/declared commands and pass; Alignment must cover every frozen criterion. Iterations/active time use the minimum of frozen workload and line limits. “Independent verification” means executed separately from agent assertions, not a separate trusted OS principal or proof the tests are adequate. [Runner](../src/Factory/Factory.Workbench/DevelopmentMissionRunner.cs#L112-L169), [Alignment validation](../src/Factory/Factory.Contracts/DevelopmentMissions.cs#L62-L98). |
| Publication | Exact candidate intent is durable before write credentials/publication; Git checks checkout, branch, remote and config, and verifies candidate cleanliness. Service verifies remote head and matching PR receipt. [Intent](../src/Factory/Factory.Workbench/DevelopmentMissionRunner.cs#L155-L167), [service gate](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L300-L319), [Git ownership](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L157-L202), [draft PR](../src/Factory/Factory.Plant.Service/GitHubDevelopmentRepository.cs#L87-L114). |
| Review and next issue | Service filters feedback to trusted reviewers and independently checks latest current-candidate approval/changes requests. Approval alone does not complete work. Only independently verified merge of the exact owned PR/candidate **plus host-confirmed worker termination** permits the next claim. Closed-unmerged/blocked/uncertain work retains ownership. [Review/progress validation](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L322-L399), [queue gate](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L74-L95), [termination receipt](../src/Factory/Factory.Plant.Service/EnrollmentMissions.cs#L427-L448). |

The service independently verifies **GitHub merge state**, but trusts the authenticated host's termination report; it does not remotely attest the OS. SDK sessions close before human review waits, which remain bounded by the mission deadline. Factory performs no automatic merge, product acceptance, release or deployment. A merged-PR workflow completion is not human authorization to release a product. [Wait behavior](../src/Factory/Factory.Workbench/DevelopmentMissionRunner.cs#L56-L104), [published PR message](../src/Factory/Factory.Plant.Service/GitHubDevelopmentRepository.cs#L103-L111).

## 7. What the worker contains—and what it does not

The supported worker uses an installation-owned WSL2 distribution named `Factory-<installation ID>`, registered under the dedicated service identity, with Linux user `worker` and workspace `/home/worker/work`. Preparation uses root inside that owned distribution to install prerequisites/payload; normal readiness and work run as the unprivileged user. The bootstrap disables Windows drive automount, interop and Windows PATH propagation. Runtime checks preparation under the actual service identity. [Environment ownership and setup](../src/Factory/Factory.Workbench/WslWorkerEnvironment.cs#L97-L120), [preparation](../src/Factory/Factory.Workbench/WslWorkerEnvironment.cs#L177-L205), [registration ownership](../src/Factory/Factory.Workbench/WslWorkerEnvironment.cs#L245-L260), [bootstrap](../src/Factory/Factory.Workbench/WslWorkerEnvironment.cs#L309-L318), [service identity check](../src/Factory/Factory.Workbench.Host/WorkbenchRuntime.cs#L146-L158).

SDK permission callbacks permit contained file reads and Implementation writes; Alignment gets reads only. Managed approvals, sandbox bypass, shell, network and other permission-request kinds are denied. Path checks reject traversal, Git/Factory metadata and filesystem links. These are SDK tool permissions, not general Linux access controls. [MissionPermissionPolicy](../src/Factory/Factory.Workbench.Worker/CopilotMissionAgent.cs#L126-L175).

Repository-defined preparation and verification commands still run executable code as the same Linux worker user. Bare executable names, bounded argument arrays, contained working directories, timeouts and bounded output are enforced; they are **not a command allowlist or an OS/network sandbox**. Processes inherit the worker environment except for explicit command-specific overrides. Implementation, verification, Alignment and their retained artifacts share the worker account/workspace/environment. Read-only Alignment tools do not create an independent machine or protect against every side effect from prior project execution. [Command validation](../src/Factory/Factory.Contracts/RepositoryWorkloads.cs#L88-L105), [process environment/limits](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L21-L85), [working directory](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L204-L210).

The host launches a `setsid` process group, retains mission/instance/nonce/process ownership in its journal, and communicates over redirected owned stdin/stdout—not a public worker listener. On the supported installation-owned distro path, stop validates ownership, terminates the **whole owned distribution** and waits until it is no longer running. The alternate recorded-group path verifies the process/instance/nonce before signaling and checking the group. Killing Windows `wsl.exe` alone is not the termination proof. [Launch/handshake](../src/Factory/Factory.Workbench/WslMissionWorker.cs#L41-L95), [stop paths](../src/Factory/Factory.Workbench/WslMissionWorker.cs#L253-L340), [distro termination](../src/Factory/Factory.Workbench/WslWorkerEnvironment.cs#L233-L260), [owned Linux group](../src/Factory/Factory.Workbench.Worker/OwnedLinuxProcess.cs#L19-L58).

Dedicated identity, unprivileged Linux user and owned WSL reduce accidental authority sharing; none establishes complete host isolation, per-process credential secrecy or a proven hostile-code sandbox. The current unsigned development installer also does not establish publisher authenticity: hashes/package consistency are not signatures. [Explicit unsigned-package gate](../src/Factory/Factory.Workbench.Setup/Program.cs#L162-L175), [MSI status](workbench-installation-test.md#L38-L47).

## 8. Revocation and recovery are not instantaneous rollback

| Revocation action | What changes | What may remain |
|---|---|---|
| Remove/change membership or plant line binding | Current service authority fails; host observes mismatch and stops old execution. Binding/membership versions invalidate saved local consent, so the new assignment requires local Start again. | Poll/contact and shutdown delay; old claim, workspace and result journals; uncertain publication/termination can block progression. [Assignment](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L26-L66), [host observe/recover](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L15-L21), [recovery](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L151-L180). |
| Disable line / explicit global shutdown / remove active mission authority | Service refuses new claims/credentials/authorization. Host/profile checks and active authorization monitoring stop execution. | Not a synchronous fleet kill. Host polls normally every 15 seconds; active worker authorization checks normally every 10 seconds with bounded contact, and OS stop/reconciliation takes additional time. No guaranteed fleet-wide revocation deadline is established. [Host loop/failure handling](../src/Factory/Factory.Workbench.Host/WorkbenchRuntime.cs#L96-L129), [monitor](../src/Factory/Factory.Workbench/DevelopmentMissionRunner.cs#L171-L204). |
| Local Stop line | Consent removed and local execution disabled before stop/reconciliation. | Stored allowlist/permission flags, enrollment and journals; unresolved termination raises an error and does not authorize another claim. [Stop](../src/Factory/Factory.Workbench.Setup/WorkbenchExecutionSetup.cs#L69-L88). |
| Disable repository or Copilot connection | New Factory grants/current authorization fail. | Issued external tokens until expiry/external revocation; remote effects and vault secret versions remain. See sections 3–4. |
| Retire enrollment | Administrator, or exact authenticated machine retiring itself, changes state to `revoking`/`retirementNeedsAttention`; ordinary machine authorization immediately rejects those states. Background processor deletes recorded owned principal/application. | Graph deletion can be delayed, fail or require administrator reconciliation. Revocation of Factory access is separate from completed directory cleanup. [Retirement request](../src/Factory/Factory.Plant.Service/EnrollmentRetirement.cs#L29-L104), [machine admission](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L9-L17), [retirement processor](../src/Factory/Factory.Plant.Service/EnrollmentRetirementProcessor.cs#L10-L86). |
| Remove Entra role / Azure RBAC / external authorization | Affects future entry or resource/token access according to that system. | Existing cookies, bearer tokens, caches and already-completed effects are not undone by the repository's policy code. Refresh/revalidation and external propagation have separate lifetimes. |

Uncertain starts, one-use credential exchanges and publication effects are not automatically replayed. Retained evidence and ownership are deliberate: deleting journals or treating a timeout as proof of stop can create duplicate work or hide a remote side effect. Recovery confirms stop and reports it before removing the active-worker journal. [Host recovery](../src/Factory/Factory.Workbench/WorkbenchDevelopmentExecution.cs#L151-L163), [publication reconciliation](../src/Factory/Factory.Workbench/DevelopmentMissionRunner.cs#L223-L247).

## 9. Other repository code is not the same permission system

`src\Workbench` retains the older Foundry distributor. Its administrator-configured `Coordinator:Operators` and `Coordinator:Workbenches` map distinct Foundry caller IDs to operator/device roles. In hosted mode it consumes Foundry-supplied caller context; explicit local mode instead checks distinct local bearer tokens and rejects platform identity headers. It does not use Factory enrollment, production-line consent or managed GitHub connections. [CoordinatorAccess.cs](../src/Workbench/Workbench.Distributor/CoordinatorAccess.cs#L13-L72).

Its daemon separately caps repositories, shell and publication through local policy. Its agent callback can approve shell requests when allowed, and its file boundary uses lexical paths rather than Factory's link checks. Its Copilot client uses the older runtime authentication path, not Factory's service token-provider setup. Do not transfer Factory's stronger/different checks to claims about that code. [DaemonPolicy](../src/Workbench/Workbench.Host/WorkbenchDaemon.cs#L5-L17), [legacy SDK and permissions](../src/Workbench/Workbench.Worker/CopilotAgentRunner.cs#L19-L43), [lexical policy](../src/Workbench/Workbench.Worker/CopilotAgentRunner.cs#L82-L108).

This document does not inventory live GitHub collaborators, organization administrators, branch rules, Azure operator assignments or legacy Foundry deployment permissions. Repository writers can change future executable workload inputs subject to the actual GitHub controls; those controls were not queried. Production-method descriptions/examples are inputs, not grants independent of the runtime checks.

## 10. Recorded discussion: central control, retooling and the decision

This section records the commissioning conversation, not shipped capabilities or a newly approved design. Code links establish the current behavior against which the alternatives were discussed.

### Where Start/Stop lives today

The user asked whether Start/Stop was central or workbench-side. **At that discussion, the local workbench Setup buttons were unshipped; they are now packaged in `0.1.9`.** Website line enablement is an independent central gate; it neither presses Start on devices nor supplies their local consent. No complete central operational Start/Stop/control implementation was added. [Local buttons](../src/Factory/Factory.Workbench.Installer/SetupForm.cs#L60-L70), [website enablement](../src/Factory/Factory.Plant.Service/Views/ProductionLines/Edit.cshtml#L63-L74).

### Retooling under the current implementation

Changing a plant's line affects every workbench assigned to it. Old authority is revoked and observed old work is stopped; the new `BindingVersion` no longer matches local consent, so **each workbench needs a new local Start line**. Retooling just one workbench currently means moving it between plants with different line bindings, rather than assigning a separate line directly to that workbench. Returning to a previous plant/binding does not revive consent under an old membership version. Uncertain stops/reconciliation retain ownership and can block progression. [Plant-wide binding/discovery](../src/Factory/Factory.Plant.Service/ProductionLineOperations.cs#L135-L200), [membership versions](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L39-L66), [consent match](../src/Factory/Factory.Contracts/WorkbenchConfiguration.cs#L73-L91).

### Alternative discussed: durable central-management delegation

One alternative was explicit delegation at enrollment or through managed-device policy: a device administrator would grant Factory durable, **revocable** authority to accept centrally approved production lines within device limits. Line approval, assignment, Start and Stop could then be central while enforcement stayed local.

That would be a persistent device delegation, not a temporary permission merely because access tokens expire. Existing enrollment must **not retroactively count as code-execution permission**. Repository installation and the owner's Copilot account authorization would remain separate. This alternative was discussed, **not implemented and not approved for rollout**.

### Fleet threat discussed

Compromise of a Factory account with authority over lines/devices—or compromise of the service control plane—can allow attacker-directed code across its authorized fleet, subject to actual device permissions. Local Start is meaningful but does not remove every way a compromised Factory/source can influence **already-approved** work: current consent does not freeze all future line/profile/repository content, and project commands execute code.

A stolen Copilot token alone grants neither Factory fleet administration nor sibling enrollment identity. Compromise of one enrollment is not supposed to authorize other devices, because service checks bind the exact application/principal/enrollment. Dedicated service identity and owned WSL do not erase fleet blast radius. These are consequences of the authority map, not findings of an exploit audit. [Exact device check](../src/Factory/Factory.Plant.Service/EnrollmentAssignment.cs#L9-L17), [consent fields](../src/Factory/Factory.Contracts/WorkbenchConfiguration.cs#L73-L91), [project execution](../src/Factory/Factory.Workbench.Worker/GitMissionRepository.cs#L204-L210).

### Alternative the user preferred exploring: independent device administration

The preferred alternative to explore was retaining device-side grants but automating them through an **independent** administrative channel, such as organization device management or PowerShell Remoting. A supported narrow noninteractive configuration command could avoid machine visits/manual JSON and bind a grant to reviewed line configuration and permissions—not merely a mutable line ID.

No such supported remoting/configuration command exists in this increment. `WorkbenchExecutionSetup` is an internal Setup helper, not an exposed supported remote command. The graphical entry accepts no line-management command arguments, and the Setup CLI switch exposes service lifecycle operations, not a supported remote Start-line interface. [Installer arguments](../src/Factory/Factory.Workbench.Installer/Program.cs#L25-L33), [CLI operations](../src/Factory/Factory.Workbench.Setup/Program.cs#L162-L196).

The security benefit would come from **independent device-administrator authority**: Factory must not hold that channel's management credentials or automatically approve its own retooling. Compromise of the management channel can itself affect the fleet. Whether approval binds an exact immutable configuration, and how normal issue/source/workload evolution would fit that approval, remains a design decision—not a silently settled implementation.

### Open alternative: temporary authorization for a fleet change

The user also wondered whether retooling could use a temporary grant, noting that adding/removing machines needs consideration too. A short-lived, scoped authorization to admit/add, remove or retool **selected devices** would authorize a particular management change—not define the lifetime of the resulting persistent assignment/execution permission, and not merely set credential TTL. Expiry would close the change window; it would not automatically undo assignments or prove an offline worker stopped. Independently approved exact targets and changes would be needed to avoid giving Factory blanket fleet-administrator authority during that window. This remains an **open alternative**, with no implementation or rollout authorized; the preserve-current-code decision below still applies.

### Final decision and unresolved questions

**“let's leave things as is.”** Preserve the local Start/Stop implementation and its assignment-scoped consent. Do not implement central operational control, blanket enrollment delegation, remote PowerShell, expanded permissions or a new rollout as part of the original documentation commission. The separately authorized release is recorded in section 11.

Open questions and acceptance gaps remain:

- Whether any future noninteractive device-grant interface should exist, who independently administers it, and exactly which immutable configuration/permissions it would approve.
- How reviewed configuration approval would coexist with intended continuous source, issue and production-method evolution. Current consent is **not** that exact-content approval.
- Real Copilot/model entitlement and renewal acceptance, real project verification/eligible inputs, and full mission execution/stop/merge/next-claim acceptance; readiness reports and local tests do not establish these.
- Operational revocation/stop latency, unresolved-claim recovery, credential/certificate rotation and cleanup, signed production packaging, and containment appropriate to the trust placed in project code. No new solution or rollout is selected here.
- Live external grant/branch-policy inventory and actual device policy remain unverified. The later App Service configuration-preservation checks are not a complete security inventory.

The original documentation commission authored only this document, without implementation, permission, deployment or machine changes. The subsequent release was separately authorized; its scope and evidence follow.

## 11. Retooling gap and subsequent release scope

After this document was commissioned, the user identified retooling as a missing
managed transition, rather than merely an assignment or consent change.
Enrollment establishes identity; assignment expresses desired work; retooling
would make the device safely authorized and prepared to execute that assignment.

The gap includes owned-work stop/reconciliation, target configuration and
permission approval, tool/configuration/credential changes, readiness evidence
and interrupted-transition recovery. Existing mechanisms provide parts of this
flow but do not establish an independently approved, exact-configuration
retooling operation. It remains open how such approval would coexist with normal
continuous issue, source and production-method evolution.

The user then authorized documenting this gap, deploying the **existing**
updated Azure service and building a matching MSI. That release authorization
does not select any of the alternatives in section 10, change the current local
Start/Stop consent model, expand permissions or enable execution. The original
documentation-only decision in section 10 describes that earlier task, not a
prohibition on the subsequently authorized release.

That release is now complete: service deployment
`cfca26c6-d010-41a7-8d52-9f9e5ff4f279` is RuntimeSuccessful and matching unsigned
MSI `0.1.9` / `workbench-0.1.9-managed-001` is built and statically verified,
not installed by the assistant. Existing settings, identities, network/auth and
vault grants were preserved. Execution remains off; no local consent, central
delegation or remote-administration capability was enabled. See the
[installation handoff](workbench-installation-test.md) for exact evidence and
outstanding acceptance.
