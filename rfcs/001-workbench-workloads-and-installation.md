# RFC 001: Workbench Workloads and Installation

Status: Draft proposal. This specifies work to build, not commands or capabilities already available.

Companion: [RFC 002: Plant Service](002-plant-service.md).

Project structure: [RFC 003: Factory Solution and Projects](003-factory-solution-and-projects.md).

## Purpose and Scope

This RFC specifies the workbench component of Factory, not the complete factory application.

A workbench is an installed client that registers with a plant and acts as a **metaruntime**: it configures and runs the Copilot SDK according to production-line specifications. The client includes setup, the Windows host, and the Linux worker; it is not a second host alongside the existing workbench.

A workbench hosts the development inner loop: prepare a checkout, run a coding agent, build and test the change, and return evidence. Publication of a branch and PR is an explicitly permitted output, not an implicit consequence of running an agent.

The supported machine is Windows x64 with Ubuntu under WSL. The Windows host maintains the plant connection and local execution state. The Linux worker runs MAF, the Copilot SDK/runtime, repository tools, and verification. Other operating systems and automatic VM provisioning are outside this RFC's scope.

The operating target is an unattended workbench: after initial installation and authorized enrollment, it starts at machine boot and sustains operation without a user signing in. Routine process failures, network outages, and credential renewal must not require human interaction. Lost identity, revoked access, and uncertain external effects remain explicit faults requiring authorized repair; self-recovery must not weaken permissions or replay work.

## Plant and Production-Line Boundary

The plant owns enrolled machines, available execution capabilities, assignment delivery, claims, and results. A production line supplies agent roles, repositories, workloads, production methods, and acceptance criteria.

Installing a workbench must not permanently bind it to a product repository or a line coordinator. Line configuration comes from the plant service and is resolved into an immutable assignment snapshot. The installed software supplies the implementations that understand that snapshot.

Workbenches initiate registration with a plant through local setup. A plant owner or user with `Workbench.Registrar` authority authorizes that registration during setup; there is no separate approver or pending-approval path. A tenant sign-in without plant-scoped registration authority is insufficient.

Registration has three dimensions:

| Dimension | Responsibility |
|---|---|
| Join a plant | Local setup signs the user in, selects a plant, and enrolls the machine identity under that user's plant authority. |
| Register capabilities | The running workbench authenticates as that machine and reports its installed runtime, versions, and supported capabilities. |
| Report presence | The workbench reports readiness and activity; the plant records its last contact. |

Enrollment and capability registration are distinct technical steps in one guided registration experience. Runtime registration does not create admission or permanently select a production line. The plant delivers assignments only to admitted workbenches with compatible capabilities.

The website is read-only for workbench registration: it displays registered machines, capabilities, and status. It does not create workbenches or approve enrollment requests. Production-line configuration is supplied afterward through assignments, not selected as part of machine registration.

The effective permission set is the intersection of local machine limits, server-approved line bindings, and assignment requests. A server setting cannot widen local permissions. One workbench may serve several lines sequentially, but only where their code and credentials belong to the same trust boundary.

## Production-Line Retooling

Retooling means configuring an execution from its production-line specification, not modifying the Copilot SDK or reinstalling the workbench. The assignment snapshot supplies the line-specific inputs; the installed client translates them into a configured MAF/Copilot session and the surrounding execution steps.

| Specification input | Workbench behavior |
|---|---|
| Worker role, instructions, guidance, and skills | Supply the selected worker's instructions to the session. |
| Agent adapter and model | Select a supported implementation and configure the requested model. |
| Required tools and integrations | Bind installed implementations and locally provisioned credentials; reject missing or unsupported capabilities. |
| Repository, base commit, and work | Prepare the assignment workspace and supply the task and acceptance criteria. |
| Requested permissions | Apply local limits and server-approved line bindings through the execution policy and SDK permission callbacks. |
| Verification, publication, and expected outputs | Configure the workbench-owned checks, permitted publication steps, and result collection, not just agent instructions. |

Build each execution's configuration from its fixed snapshot. Do not carry another assignment's instructions, tools, or session state into it. Later production-line changes apply to new assignments, not work already underway.

The existing MAF/Copilot adapter remains the coding-agent integration. Copilot owns the reasoning and tool loop; the metaruntime owns configuration and execution boundaries, not a second reasoning loop. A line specification may select supported capabilities but cannot install arbitrary executable extensions, supply credentials, or grant additional permissions.

## Workload Contract

Each accepted assignment identifies:

| Input | Required content |
|---|---|
| Identity | Plant, workbench, line and revision, assignment, production/run, and worker role. |
| Source | Supported source-control provider, repository, base branch, and resolved base commit. |
| Work | Instructions, guidance, skills, acceptance criteria, and expected outputs. |
| Execution | Supported agent adapter, model selection where applicable, required tools, timeout, and resource requirements. |
| Verification | Executables and argument arrays, working directory relative to the checkout, and bounded output capture. |
| Publication | Whether publication is allowed; branch and PR requirements when requested. |
| Integrations | Configuration revision, telemetry routing, artifact policy, and logical credential references. No credentials. |

The service resolves mutable line settings before enqueueing. It stores the snapshot and its digest; the host checks the digest and supported schema before execution. Execution checks out the resolved commit rather than silently taking a newer branch tip. A changed input requires a new assignment, not mutation of a claimed one.

The submitter supplies the resolved base commit; the worker verifies it against the repository during checkout. Queueing alone does not require the plant service to hold GitHub credentials.

The worker supports this bounded sequence:

1. Validate the snapshot, repository, tools, credentials, permissions, and available disk space.
2. Create an assignment-specific workspace and checkout. Reject an existing workspace rather than overwriting or replaying it.
3. Perform declared dependency preparation, configure the MAF/Copilot session from the snapshot, then start the coding worker.
4. Allow the agent to edit and run permitted tools inside the intended checkout.
5. Run the assignment's verification independently of the agent's final response.
6. If changes and required checks are satisfactory, create the commit. Push and create a PR only when publication is permitted.
7. Read back the published repository, branch, commit, base, and PR state. Persist the terminal result locally before reporting it.

Dependency preparation may run repository code. It requires the same explicit execution permission as other shell work. It must not be disguised as an installation precheck.

The initial host executes one assignment at a time across all lines. It does not choose the best workbench, merge PRs, authorize releases, or automatically replay interrupted work. An agent's success message is not verification evidence.

## Integration Boundaries

Separate source-control/publication operations, agent execution, and telemetry export from the execution sequence. Provide GitHub and MAF/Copilot implementations. Unsupported provider identifiers fail validation; they do not trigger downloads of executable extensions.

The server supplies repository targets, line instructions, publication requirements, and approved telemetry routing. The workbench advertises supported adapters and versions. Changing line configuration must not require reinstalling a machine unless a required implementation is missing.

The default telemetry route is workbench to plant service, then Azure Monitor. Other destinations require an implemented exporter and operator-approved destination. Configuration is not permission to send source or credentials to an arbitrary URL.

## Installation Interface

Provide a signed Windows installer with a guided setup entry point for matching host and worker packages, an Installed Apps entry, and uninstall support. Install a machine-scoped Windows Service under a dedicated execution identity. Packaging alone does not provide unattended operation; the service, worker environment, credentials, and recovery behavior must meet the requirements below.

Initial installation, enrollment, and exceptional repair may require an authorized person. Routine startup, reconnection, credential renewal, and approved maintenance must not depend on that person's session. Request elevation only for machine-scoped changes that require it.

Setup supports the following actions:

| Action | Behavior |
|---|---|
| Inspect | Read-only checks of the package, machine, WSL user, tools, configuration, credentials, and connectivity. |
| Configure | Prompt for missing values, show existing selections, and persist non-secret configuration. |
| Install | Show required changes, apply approved dependency repairs, stage matching host/worker versions, and install the service under its dedicated identity. |
| Enroll | Sign the user in locally, select a plant, register under the user's plant authority without separate approval, and verify the provisioned daemon identity. |
| Verify | Check connection readiness separately from the requested coding/publication workload. |
| Start / Stop | Control the Windows Service and its owned worker processes. An intentional stop must not be treated as a crash requiring restart. Foreground execution is diagnostic only and must not run alongside the service. |
| Upgrade / Rollback | Drain execution, validate compatibility, and switch matching host/worker releases without replacing identity or journals. |
| Uninstall / Retire | Remove installed software separately from retiring the plant identity or purging state. Preserve identity and journals by default. |

Every action reports passed, failed, unverified, or not applicable, with a specific repair. A failed probe is not proof that a dependency is absent. Inspection must not install tools, sign in, modify Git configuration, or submit work.

Connection verification uses registration/status operations without starting the claim loop. Claims begin only after execution is explicitly enabled under a configured local policy and service recovery/readiness checks pass. Subsequent boots do not require renewed human approval. An enrollment check must not accidentally pick up a coding assignment.

### Delivery Sequence

The current Factory implementation includes an unsigned development MSI, graphical Entra sign-in and plant selection, resumable enrollment, and certificate-authenticated activation/registration/presence. The Windows Service uses a non-exportable machine CNG key under its dedicated virtual service account and persists outstanding reports before sending them. MSI maintenance updates an existing runtime and supports rollback and state-preserving uninstall. The website remains read-only for workbench registration. Production signing, automatic certificate rotation, live Entra/Graph and cold-boot acceptance, service-owned WSL, and worker execution remain incomplete; this is not yet a production-qualified unattended installation.

Establish service-owned WSL and credential support, then deliver signed installation, enrollment, boot-time authentication, registration, and readiness reporting before adding assignment delivery and production-line execution to the new plant connection. The connection-only slice must not claim work or run a coding task.

The plant service currently supports registration and presence for already-admitted workbenches. Enrollment authorization and identity provisioning are server prerequisites for guided setup, not capabilities a client wrapper can provide alone. The existing Foundry distributor connection is not the new plant API connection.

Setup automates the approved enrollment and credential configuration steps. Runtime acquires and renews plant access tokens using the workbench's own identity, independently of the enrolling user's session. GitHub repository access and Copilot model access are checked separately inside WSL; plant enrollment grants neither.

### Automated Installation Sequence

| Step | Required behavior |
|---|---|
| Verify package | Validate a signed release manifest against the trusted publisher and check file digests. Report host, worker, protocol, schema, and bundled runtime versions. Reject mixed packages. |
| Select context | Confirm plant URL, expected tenant/API identity, dedicated service identity, its WSL distribution/user, storage locations, and local permission limits. Keep the enrolling human distinct from the runtime identity. Reuse an existing installation when present. |
| Check machine | Test architecture, WSL availability under the service identity, distribution state, non-interactive tool discovery, writable paths, disk space, clock, TLS, and required outbound endpoints. A successful check in the installer's session does not prove service readiness. |
| Repair prerequisites | Offer versioned, supported installation steps for Windows/WSL runtimes and tools. Request elevation only for the step needing it. Record restart requirements and resume without recreating identity. |
| Install binaries | Stage Windows and Linux releases, check the installed executables under the runtime identity, and activate matching versions only after both pass checks. Install the service without enabling work before enrollment and readiness checks succeed. |
| Enroll identity | Follow the credential flow below. Do not ask the user to assemble certificate passwords, caller IDs, or role IDs. |
| Check workload | Provision and probe supported unattended GitHub/Copilot credentials and Git author configuration under the service-owned WSL user. Do not rely on the installer's interactive credential cache. |
| Verify execution | Offer an explicitly approved agent/verification smoke test, then a separate publication test if requested. State possible model charges and repository changes before running them. |
| Enable startup | After connection verification, configure automatic service start at machine boot and bounded failure recovery. Show the service identity, distribution, permissions, and executable. Verify cold-boot operation without a user signing in before reporting unattended readiness. |
| Report | Write a redacted local report and show the exact device, service identity, versions, readiness levels, and unresolved checks. Confirm server-side presence independently of process startup. |

Workload prerequisites are separate from base installation. A line may require Node, a .NET SDK, Python, browsers, or other tools; these are declared and version-checked rather than installed speculatively. Changes to package sources or execution of repository setup scripts require approval.

The packaged Copilot executable is an SDK runtime, not the interactive CLI. Setup must not invoke `copilot login` against it. Check the pinned SDK's supported authentication path and verify it through that runtime.

## Local Configuration and Startup

Persist non-secret machine configuration under `%ProgramData%\Factory\Workbench\config.json`: service URL, expected tenant/API audience, installation and plant/workbench IDs, service identity, WSL distribution and user, client ID, certificate location/thumbprint, local repository/integration limits, paths, and active configuration revision. Validate the schema and write atomically. Restrict changes to authorized setup/administration and the service's required operations.

Keep Windows binaries in versioned release directories beneath `%ProgramFiles%\Factory\Workbench\releases`, protected from worker modification. Keep service journals, results, and bounded logs beneath `%ProgramData%\Factory\Workbench`, outside release directories and with appropriate access controls. Apply the same release/state separation to the Linux installation. An upgrade changes the selected release, not the identity or journals.

The managed host is a Windows Service configured for automatic boot-time startup and bounded restart after crashes. It runs under a dedicated, least-privileged identity with explicit access to its machine credential, state, and WSL environment. It must not depend on a developer's profile, mapped drives, interactive environment, or logon task. Do not select LocalSystem merely to bypass identity or WSL setup.

Allow only one host for a workbench/state directory, including across service and diagnostic sessions. Startup checks local recovery and persisted maintenance state before permitting claims. A running service is not proof of readiness: distinguish process liveness, plant connectivity, execution readiness, and recovery faults. Report successful registration and advancing server-observed contact independently of process startup.

### Service-Owned WSL Acceptance Gate

The existing user-owned WSL arrangement is not evidence that unattended hosting works. Before selecting the production service-account configuration, demonstrate that the actual dedicated Windows identity can start and manage its distribution, select the intended Linux user, access its tools and credentials, stop owned processes, and recover after a cold reboot with nobody logged in.

Verify repository and Copilot authentication under that same environment without an interactive login prompt or a developer's credential cache. Demonstrate machine-certificate renewal through narrowly authorized local tooling without an interactive elevation prompt. Record the supported service-account, WSL, profile-loading, and credential-provisioning requirements. If this cannot be demonstrated, the Windows/WSL hosting design must be revised before unattended operation is declared supported; an MSI or interactive scheduled task is not a substitute.

## Configuration Revisions, Upgrades, and Removal

Configuration changes are explicit revisions. Validate a candidate before activation, record the active revision, and apply execution-affecting changes at an idle boundary. An accepted assignment retains its snapshot and must not gain permissions through a configuration change. Recover interrupted configuration writes without replacing identity or journals.

Upgrades use signed, approved packages and an explicit maintenance policy. Stop accepting assignments, drain the active execution, and persist pending reports before switching releases. Stage matching host/worker versions, check protocol and state compatibility, and record activation durably so interruption leaves a recoverable selected release. Apply elevated package changes through authorized installation tooling, not by granting the worker write access to installed binaries. Resume claims only after recovery and readiness checks succeed.

Keep the previous compatible release for rollback after failed activation. Rollback must not downgrade incompatible configuration/journal formats, recreate identity, discard results, or replay work. An unresolved assignment blocks an unsafe switch. Do not force-kill work to meet a maintenance window; defer the upgrade and report the reason unless an authorized cancellation has been reconciled.

Uninstall disables automatic startup, stops the service and its owned processes safely, and removes only this installation's registered service and binaries. Preserve configuration, identity, journals, and results by default so reinstall can recover the same workbench. Do not implicitly unregister a WSL distribution or remove shared prerequisites.

Retiring the plant identity and purging retained local state are separate, explicitly authorized actions. Reconcile unresolved work before destructive removal. If the plant is unavailable, report retirement as incomplete; removing local software must not be reported as server-side revocation.

## Authorization and Credential Flow

Use three separate authorities:

| Authority | Purpose |
|---|---|
| Human Entra sign-in | Select an accessible plant and authorize initial enrollment or exceptional recovery under the user's plant-scoped role. Not a runtime dependency. |
| Workbench Entra application and local certificate | Unattended calls to the plant API as one approved machine identity. |
| GitHub/Copilot credentials in service-owned WSL | Unattended repository access, optional publication, and model access. These are not supplied by plant enrollment or inherited from the enrolling human. |

The first version uses one Entra application/service principal per workbench. Its only application permission is the plant API's `Workbench.Connect` role. It receives no Cosmos DB, Blob Storage, Key Vault, Microsoft Graph, or Azure management permissions.

### One-Time Plant Preparation

An authorized administrator provisions the web application/API and installer registrations described in RFC 002, consents their permissions, and configures who may create plants. A plant has an Entra user owner who can assign plant-scoped roles, including workbench registration. Azure resource deployment, directory administration, and plant ownership are distinct authorities.

Publish a trusted installation profile containing the service URL and expected tenant/API/client IDs. The installer may discover matching public metadata from the service, but must not silently trust a different tenant or audience returned by an arbitrary URL.

Grant the service's background provisioning identity the administrator-approved Graph application permissions specified in RFC 002. This one-time directory setup enables enrollment without asking each user to administer Entra. The installer and daemon receive no Graph permissions, and the service does not retain the enrolling user's token to perform directory updates.

### Enrollment

1. The installer confirms the destination and signs the human into the configured tenant using MSAL. Reuse the Windows account through supported broker authentication where available; otherwise use browser authorization-code flow with PKCE. Device-code flow is an explicit fallback where tenant policy allows it. Conditional Access failures remain errors, not reasons to weaken policy.
2. List the plants accessible to that user and select a target where the user has owner or `Workbench.Registrar` authority. A valid tenant sign-in or plant read access alone does not permit registration; users without registration authority cannot submit an enrollment request.
3. Use authorized local setup/service tooling to create a non-exportable RSA certificate in the Windows LocalMachine certificate store, using the Windows key provider. Restrict private-key access to the dedicated service identity and authorized credential-management tooling, not the enrolling user's session. Proposed validity is 180 days. This is a client credential, not a TLS server certificate; only the public certificate leaves the machine.
4. Obtain a short-lived server challenge. Submit the plant, chosen device name, installation identifier, public certificate, advertised capabilities, and a signature binding the challenge to the enrollment request. The server verifies possession, expiry, requester authority, and replay protection.
5. After verifying the user's plant-scoped registration authority, commit an enrollment operation and return `202 Accepted` with its identifier and status URL. The user's explicit submission authorizes enrollment; the asynchronous operation waits for provisioning, not a second person's approval.
6. A service-owned background worker takes an ETag-guarded reservation on an authorized operation. Using its own identity, it creates or resumes the dedicated Entra application/service principal, uploads the public certificate, and assigns `Workbench.Connect` on the plant API. It records directory IDs and progress against the operation; uncertain Graph responses require reconciliation, not another application or concurrent provisioner.
7. The service finalizes the binding between plant, tenant, service-principal object ID, client ID, and server-assigned workbench ID. Recheck the enrolling user's current plant registration authority before provisioning and activation. Directory provisioning and Cosmos updates are not one transaction; intermediate states remain non-executable.
8. The installer polls the same operation using the enrolling user's delegated sign-in. After an interruption, reuse the operation and local key rather than submitting again. The background work does not require the user's token or browser session to stay alive.
9. The installer receives and persists the non-secret binding. The Windows host, running under the actual service identity, acquires an application token using its local certificate for `api://<plant-api-client-id>/.default`. The plant service validates the actual token identity against the approved record before activating the device.
10. The host registers installed capabilities and versions. Setup verifies that the plant reports this exact workbench online, then discards its enrollment user-token session. Unattended readiness additionally requires the cold-boot checks above.

Directory propagation is handled with bounded retries and useful status, not repeated provisioning. A denied or failed enrollment does not enable startup. The remaining request and key are shown explicitly for retry or approved removal.

The normal path needs no operator script, directory administrator at the device, or copied Entra identifiers. Missing provisioning consent is a visible service configuration error, not a reason to grant directory privileges to the enrolling user. The installer reports enrollment authorization failures, provisioning retries, and activation outcomes. The website displays the resulting workbench inventory and status without registration or approval controls.

The daemon authenticates independently after activation. Restarts reuse the established machine identity and plant binding, refresh capability registration, and resume presence reporting without another human sign-in or a new enrollment. It does not impersonate the enrolling user or inherit their broader permissions. Removing the user's plant role prevents future authorized enrollment actions but does not silently delete an already admitted workbench; device revocation is explicit.

### Runtime, Rotation, and Revocation

The host loads its certificate from the configured machine store by thumbprint and uses an explicit certificate credential. Token acquisition and renewal run under the service identity without human sign-in. It must not silently fall back to the operator's Azure CLI login or another environment credential. No account password, PFX password, private key, or bearer token belongs in configuration, assignment snapshots, logs, or shell history. Any required service-account secret must use a supported Windows-managed protection mechanism.

Renewal begins 30 days before expiry. The daemon generates a replacement key locally and requests rotation with proof of both old and new key possession. The service's background provisioner adds the public credential to the existing application. Verify new-key possession and fresh application-token authentication before retiring the old credential; preserve the service-principal/workbench binding. An expired or lost key requires a plant-authorized user's recovery approval, not automatic identity recreation.

Revocation first disables admission in the plant record, so an already-issued token cannot continue to claim work. Background provisioning then removes the directory role/credential as appropriate. Revocation does not kill a disconnected process or undo a GitHub operation; cancellation and reconciliation remain separate.

### Repository and Model Credentials

Provision and check credentials inside the service-owned WSL distribution as the actual worker user, including after a cold boot without interactive logon. A successful check in the installer's Windows or WSL session proves nothing about that environment.

For the initial GitHub adapter, configure supported unattended repository/publication authentication, Git credential integration, and Git author identity after authorization. Verify read access to the selected repository as the runtime identity. Publication readiness additionally checks that identity's reported push/PR permissions; only an approved publication exercise proves the full path. Do not make a developer's interactive `gh` login the daemon's credential source.

Use an explicitly supported unattended authentication path for the pinned Copilot SDK. Do not assume a repository token or GitHub application credential provides Copilot entitlement. Establish credential lifetime, protected storage, renewal/replacement, and cold-boot discovery for each integration before declaring it unattended-ready. An interactive login prompt or dependence on the enrolling user's session fails that requirement.

Treat Copilot entitlement/model access as a separate check from GitHub repository access. Do not send these credentials to the plant server. Logical credential references select locally provisioned access; a missing or expired credential blocks affected execution and reports a specific fault rather than prompting a background login or falling back to another identity.

WSL and an SDK permission handler are not a security sandbox. Shell code running as the worker can reach that account's files and potentially Windows facilities. Non-exportable keys reduce accidental disclosure but do not protect against a compromised runtime identity. Do not place mutually untrusted production lines on the same execution identity or environment.

## Recovery and Evidence

On boot or process restart, acquire exclusive ownership of the installation, load the selected release/configuration and machine identity, and inspect persisted execution state and any surviving owned worker. Reconnect and refresh registration/presence without reenrolling. Accept new work only after local recovery, authorization, compatibility, and workload-readiness checks pass.

Use bounded backoff with jitter for transient network/service failures and bounded service restart for crashes; avoid tight retry or crash loops. Recover automatically when dependencies return. An intentional stop or maintenance drain must not trigger crash recovery. Persist actionable faults and report them through local service diagnostics and plant presence when connectivity and authorization permit; do not rely solely on interactive dialogs or remote telemetry.

Persist claim intent before requesting work, the accepted claim before execution, and the terminal result before reporting. Retry transport and result delivery without rerunning the coding task. An interrupted assignment with no saved result blocks further work until an operator reconciles local processes, branches, and PR state.

Automatic service restart is not automatic assignment replay. Resume saved result delivery without executing the task again. Where a worker may still be alive or a commit, push, or PR outcome is uncertain, preserve the evidence and stop claims until ownership and external effects are reconciled. Lost identity, revoked access, expired credentials that cannot be renewed, and incompatible state require authorized repair, not automatic identity recreation or weaker authentication.

Use timeouts and cancellation to stop the owned worker and its process group, then verify termination before reporting cancellation as complete. A missed heartbeat is not evidence that the process stopped. Do not delete journals to make installation or upgrades succeed.

Record assignment stage, timestamps, package/configuration revisions, line/run/claim IDs, permission decisions, verification commands and exit codes, and publication read-back. Record tool events and usage only where the SDK exposes them. Do not claim access to private model reasoning or treat streamed text as a full tool trace.

Keep bounded local logs and an upload spool. Redact secrets and omit source/prompt contents by default. A telemetry outage is visible but does not erase the durable result or cause task replay.

## Completion Criteria

- A prepared machine can be installed from one signed Windows package with an Installed Apps entry and uninstall support, without remembered terminal variables.
- A plant owner or workbench registrar can enroll from the device without separate approval, directory privileges, or an operator script; users without registration authority are rejected.
- Enrollment, background Graph provisioning, and activation resume after interruption without duplicate identities or dependence on a continuing user session.
- The website shows registered workbenches, capabilities, and status without workbench creation or enrollment approval controls.
- A cold reboot with no user logon starts the Windows Service and its service-owned WSL environment, reuses the same workbench ID, certificate binding, and journals, and restores registration/presence without human sign-in.
- Plant, repository, and Copilot credentials are available under the actual runtime identity after reboot; routine renewal and reconnection require no interactive credential cache.
- Crash and network-outage recovery use bounded retries and preserve execution ownership; saved results are redelivered without task replay, and uncertain effects block new claims with an actionable fault.
- Connection, agent execution, verification, and publication readiness are reported separately.
- The connection-only delivery slice registers and reports presence without claiming or executing work.
- An approved workload reaches a verified PR; refused permissions and missing tools fail before unsafe execution.
- Two lines can use one workbench sequentially without mixing configuration, workspaces, or result attribution.
- Changing supported line instructions, skills, tools, or model selection reconfigures new executions without reinstalling the client; an in-flight assignment retains its snapshot.
- Configuration revision, upgrade, and rollback preserve identity, claims, and results; failed activation recovers a compatible selected release, and unresolved work prevents unsafe replacement or replay.
- Uninstall removes only this installation's service and binaries while preserving identity/state by default; retirement and destructive purge require explicit authorization and do not claim remote success while offline.
- Tests cover cold boot without logon, service-identity WSL/tool/credential access, service crash and intentional stop, wrong tenant/audience, unauthorized enrollment, unadmitted and revoked devices, expired certificates, credential rotation, service outage, interrupted upgrade/configuration, uninstall/reinstall, and interrupted publication.

## References

- [Microsoft identity platform device authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Microsoft Graph certificate credentials and rotation](https://learn.microsoft.com/en-us/graph/api/application-addkey?view=graph-rest-1.0)
- [Microsoft Graph application-role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0)
- [Copilot SDK authentication](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate)
