# RFC 001: Workbench Workloads and Installation

Status: Draft proposal. This specifies work to build, not commands or capabilities already available.

Companion: [RFC 002: Plant Service](002-plant-service.md).

Project structure: [RFC 003: Factory Solution and Projects](003-factory-solution-and-projects.md).

## Purpose and Scope

This RFC specifies the workbench component of Factory, not the complete factory application.

A workbench hosts the development inner loop: prepare a checkout, run a coding agent, build and test the change, and return evidence. Publication of a branch and PR is an explicitly permitted output, not an implicit consequence of running an agent.

The supported machine is Windows x64 with Ubuntu under WSL. The Windows host maintains the plant connection and local execution state. The Linux worker runs MAF, the Copilot SDK/runtime, repository tools, and verification. Other operating systems and automatic VM provisioning are outside this RFC's scope.

## Plant and Production-Line Boundary

The plant owns enrolled machines, available execution capabilities, assignment delivery, claims, and results. A production line supplies agent roles, repositories, workloads, production methods, and acceptance criteria.

Installing a workbench must not permanently bind it to a product repository or a line coordinator. Line configuration comes from the plant service and is resolved into an immutable assignment snapshot. The installed software supplies the implementations that understand that snapshot.

The effective permission set is the intersection of local machine limits, server-approved line bindings, and assignment requests. A server setting cannot widen local permissions. One workbench may serve several lines sequentially, but only where their code and credentials belong to the same trust boundary.

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
3. Perform declared dependency preparation, then start the MAF/Copilot coding worker.
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

Provide one guided Windows setup entry point for matching host and worker packages. It supports the following actions:

| Action | Behavior |
|---|---|
| Inspect | Read-only checks of the package, machine, WSL user, tools, configuration, credentials, and connectivity. |
| Configure | Prompt for missing values, show existing selections, and persist non-secret configuration. |
| Install | Show required changes, apply approved dependency repairs, and stage matching host/worker versions. |
| Enroll | Sign the user in, select a plant, register under the user's plant authority or request approval, and verify the provisioned daemon identity. |
| Verify | Check connection readiness separately from the requested coding/publication workload. |
| Start / Stop | Run in the foreground or manage the explicitly enabled startup task. Stop only the recorded process and its work. |
| Upgrade / Remove | Drain execution, preserve or explicitly retire identity/state, and change only this installation. |

Every action reports passed, failed, unverified, or not applicable, with a specific repair. A failed probe is not proof that a dependency is absent. Inspection must not install tools, sign in, modify Git configuration, or submit work.

Connection verification uses registration/status operations without starting the claim loop. Claims begin only after an explicit start with a configured local policy. An enrollment check must not accidentally pick up a coding assignment.

### Automated Installation Sequence

| Step | Required behavior |
|---|---|
| Verify package | Validate a signed release manifest against the trusted publisher and check file digests. Report host, worker, protocol, schema, and bundled runtime versions. Reject mixed packages. |
| Select context | Confirm plant URL, expected tenant/API identity, installation owner, WSL distribution/user, storage locations, and local permission limits. Reuse an existing installation when present. |
| Check machine | Test architecture, WSL availability, distribution state, non-interactive tool discovery, writable paths, disk space, clock, TLS, and required outbound endpoints. |
| Repair prerequisites | Offer versioned, supported installation steps for Windows/WSL runtimes and tools. Request elevation only for the step needing it. Record restart requirements and resume without recreating identity. |
| Install binaries | Stage Windows and Linux releases, check the installed executables, and activate matching versions only after both pass checks. |
| Enroll identity | Follow the credential flow below. Do not ask the user to assemble certificate passwords, caller IDs, or role IDs. |
| Check workload | Probe the selected repository and workload under the actual WSL user. Offer explicit Git/GitHub authentication and author-identity setup where required. |
| Verify execution | Offer an explicitly approved agent/verification smoke test, then a separate publication test if requested. State possible model charges and repository changes before running them. |
| Enable startup | After connection verification, offer user-logon startup. Show the account, distribution, permissions, and executable being enabled. |
| Report | Write a redacted local report and show the exact device, service identity, versions, readiness levels, and unresolved checks. Confirm server-side presence independently of process startup. |

Workload prerequisites are separate from base installation. A line may require Node, a .NET SDK, Python, browsers, or other tools; these are declared and version-checked rather than installed speculatively. Changes to package sources or execution of repository setup scripts require approval.

The packaged Copilot executable is an SDK runtime, not the interactive CLI. Setup must not invoke `copilot login` against it. Check the pinned SDK's supported authentication path and verify it through that runtime.

## Local Configuration and Startup

Persist non-secret machine configuration under `%LOCALAPPDATA%\Workbench\config.json`: service URL, expected tenant/API audience, plant/workbench IDs, WSL distribution and user, client ID, certificate thumbprint, local repository/integration limits, paths, and startup preference. Validate the schema and write atomically.

Keep Windows binaries in versioned release directories beneath `%LOCALAPPDATA%\Workbench\releases`. Keep daemon journals and results outside those directories. Apply the same separation to the Linux installation. An upgrade changes the selected release, not the identity or journals.

The first managed startup mode is a Windows Task Scheduler task running as the installation owner at that user's logon, without storing a Windows password. It launches the same host/configuration as foreground mode. It is not boot-time service execution: logout or machine shutdown may interrupt work. Support for a different service account or boot-time execution requires a separate WSL and credential design.

Allow only one host for a workbench/state directory. Startup records and checks its process identity. Starting a process is not proof that it connected; report successful registration and advancing server-observed contact.

## Authorization and Credential Flow

Use three separate authorities:

| Authority | Purpose |
|---|---|
| Human Entra sign-in | Select an accessible plant and authorize enrollment under the user's plant-scoped role. |
| Workbench Entra application and local certificate | Unattended calls to the plant API as one approved machine identity. |
| GitHub/Copilot credentials in WSL | Repository access, optional publication, and model access. These are not supplied by plant enrollment. |

The first version uses one Entra application/service principal per workbench. Its only application permission is the plant API's `Workbench.Connect` role. It receives no Cosmos DB, Blob Storage, Key Vault, Microsoft Graph, or Azure management permissions.

### One-Time Plant Preparation

An authorized administrator provisions the web application/API and installer registrations described in RFC 002, consents their permissions, and configures who may create plants. A plant has an Entra user owner who can assign plant-scoped roles, including workbench registration. Azure resource deployment, directory administration, and plant ownership are distinct authorities.

Publish a trusted installation profile containing the service URL and expected tenant/API/client IDs. The installer may discover matching public metadata from the service, but must not silently trust a different tenant or audience returned by an arbitrary URL.

Grant the service's background provisioning identity the administrator-approved Graph application permissions specified in RFC 002. This one-time directory setup enables enrollment without asking each user to administer Entra. The installer and daemon receive no Graph permissions, and the service does not retain the enrolling user's token to perform directory updates.

### Enrollment

1. The installer confirms the destination and signs the human into the configured tenant using MSAL. Reuse the Windows account through supported broker authentication where available; otherwise use browser authorization-code flow with PKCE. Device-code flow is an explicit fallback where tenant policy allows it. Conditional Access failures remain errors, not reasons to weaken policy.
2. List the plants accessible to that user and select the target. Show whether the user may register a workbench directly or only request approval. A valid tenant sign-in alone grants neither authority.
3. Create a non-exportable RSA certificate in the installation owner's Windows CurrentUser certificate store, using the Windows key provider and user-restricted key access. Proposed validity is 180 days. This is a client credential, not a TLS server certificate; only the public certificate leaves the machine.
4. Obtain a short-lived server challenge. Submit the plant, chosen device name, installation identifier, public certificate, advertised capabilities, and a signature binding the challenge to the enrollment request. The server verifies possession, expiry, requester authority, and replay protection.
5. Commit an enrollment operation and return `202 Accepted` with its identifier and status URL. If the user has `Workbench.Registrar` or owner authority on that plant, their explicit submission authorizes enrollment without a second approver. A user with only request authority leaves a pending request for a plant owner or registrar to approve.
6. A service-owned background worker takes an ETag-guarded reservation on an approved operation. Using its own identity, it creates or resumes the dedicated Entra application/service principal, uploads the public certificate, and assigns `Workbench.Connect` on the plant API. It records directory IDs and progress against the operation; uncertain Graph responses require reconciliation, not another application or concurrent provisioner.
7. The service finalizes the binding between plant, tenant, service-principal object ID, client ID, and server-assigned workbench ID. Recheck approval and the approving user's current plant authority before provisioning and activation. Directory provisioning and Cosmos updates are not one transaction; intermediate states remain non-executable.
8. The installer polls the same operation using the requester's delegated sign-in; the web application can show the same progress through Datastar updates. After an interruption, reuse the operation and local key rather than submitting again. The background work does not require the user's token or browser session to stay alive.
9. The installer receives the non-secret binding and acquires an application token using the local certificate for `api://<plant-api-client-id>/.default`. The service validates the actual token identity against the approved record before activating the device.
10. The host registers installed capabilities and versions. The installer verifies that the service reports this exact workbench online, then discards its enrollment user-token session.

Directory propagation is handled with bounded retries and useful status, not repeated provisioning. A denied or failed enrollment does not enable startup. The remaining request and key are shown explicitly for retry or approved removal.

The normal path needs no operator script, directory administrator at the device, or copied Entra identifiers. Missing provisioning consent is a visible service configuration error, not a reason to grant directory privileges to the enrolling user. Approval, provisioning retries, failure, and denial remain visible in both installer and web application.

The daemon authenticates independently after activation. It does not impersonate the enrolling user or inherit their broader permissions. Removing the user's plant role prevents future authorized enrollment actions but does not silently delete an already admitted workbench; device revocation is explicit.

### Runtime, Rotation, and Revocation

The host loads its certificate by thumbprint and uses an explicit certificate credential. It must not silently fall back to the operator's Azure CLI login or another environment credential. No PFX password, private key, or bearer token belongs in configuration, assignment snapshots, logs, or shell history.

Renewal begins 30 days before expiry. The daemon generates a replacement key locally and requests rotation with proof of both old and new key possession. The service's background provisioner adds the public credential to the existing application. Verify new-key possession and fresh application-token authentication before retiring the old credential; preserve the service-principal/workbench binding. An expired or lost key requires a plant-authorized user's recovery approval, not automatic identity recreation.

Revocation first disables admission in the plant record, so an already-issued token cannot continue to claim work. Background provisioning then removes the directory role/credential as appropriate. Revocation does not kill a disconnected process or undo a GitHub operation; cancellation and reconciliation remain separate.

### Repository and Model Credentials

Check credentials inside the selected WSL distribution as the actual worker user. A Windows login proves nothing about that environment.

For the initial GitHub adapter, guide the user through supported `gh` authentication, Git credential-helper setup, and Git author configuration only after approval. Verify read access to the selected repository. Publication readiness additionally checks the signed-in identity and reported push/PR permissions; only an approved publication exercise proves the full path.

Use the pinned Copilot SDK's supported credential discovery, including `gh` where supported. Treat Copilot entitlement/model access as a separate check from GitHub repository access. Do not send these credentials to the plant server. Logical credential references select locally provisioned access; a missing reference blocks execution.

WSL and an SDK permission handler are not a security sandbox. Shell code running as the owner can reach that user's files and potentially Windows facilities. Non-exportable keys reduce accidental disclosure but do not protect against a compromised owner account. Do not place mutually untrusted production lines on the same user environment.

## Recovery and Evidence

Persist claim intent before requesting work, the accepted claim before execution, and the terminal result before reporting. Retry transport and result delivery without rerunning the coding task. An interrupted assignment with no saved result blocks further work until an operator reconciles local processes, branches, and PR state.

Use timeouts and cancellation to stop the owned worker and its process group, then verify termination before reporting cancellation as complete. A missed heartbeat is not evidence that the process stopped. Do not delete journals to make installation or upgrades succeed.

Record assignment stage, timestamps, package/configuration revisions, line/run/claim IDs, permission decisions, verification commands and exit codes, and publication read-back. Record tool events and usage only where the SDK exposes them. Do not claim access to private model reasoning or treat streamed text as a full tool trace.

Keep bounded local logs and an upload spool. Redact secrets and omit source/prompt contents by default. A telemetry outage is visible but does not erase the durable result or cause task replay.

## Completion Criteria

- A prepared machine can be installed from one matching package without remembered terminal variables.
- A plant owner or workbench registrar can enroll from the device without separate directory privileges or an operator script; request-only users require approval.
- Enrollment, approval, background Graph provisioning, and activation resume after interruption without duplicate identities or dependence on a continuing user session.
- Restart uses the same workbench ID, certificate binding, and journals without manual session creation.
- Connection, agent execution, verification, and publication readiness are reported separately.
- An approved workload reaches a verified PR; refused permissions and missing tools fail before unsafe execution.
- Two lines can use one workbench sequentially without mixing configuration, workspaces, or result attribution.
- Upgrade/rollback preserves claims and results; an unresolved assignment prevents unsafe replacement or replay.
- Tests cover wrong tenant/audience, unapproved and revoked devices, expired certificates, credential rotation, service outage, and interrupted publication.

## References

- [Microsoft identity platform device authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Microsoft Graph certificate credentials and rotation](https://learn.microsoft.com/en-us/graph/api/application-addkey?view=graph-rest-1.0)
- [Microsoft Graph application-role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0)
- [Copilot SDK authentication](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate)
