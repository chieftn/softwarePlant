# Factory

ASP.NET Core/.NET 10 REST service, Razor website, and local Windows workbench setup for [RFC 003](../../rfcs/003-factory-solution-and-projects.md). The existing `src\Workbench` implementation and deployment are unchanged.

## Bootstrap

Run commands from the repository root in **PowerShell 7**. Provisioning also requires Azure CLI/Bicep and an explicitly selected tenant/subscription. Scripts prompt before cloud changes; `-WhatIf` checks inputs and describes actions without provisioning.

| Tier | Guide | Responsibility |
|---|---|---|
| 1. Tenant | [Entra bootstrap](bootstrap/tenant/README.md) | Create or adopt the application/service principal, configure sign-in and API roles/scopes, and assign selected users. |
| 2. Production | [Production backend](bootstrap/production/README.md) | Create the resource group, private Cosmos DB, managed identity and network. **Backend only; no website deployment.** |
| 3. Development | [Azure harness and local configuration](bootstrap/development/README.md) | Provision separate development Cosmos, then configure standard .NET user-secrets and local HTTPS. |

Development needs tier 1 followed by tier 3. The current production backend uses managed identity and can be provisioned independently; a production website registration is only needed when hosting is added.

[Infrastructure inventory](deploy/README.md) documents the deployed resource definitions, identity/network boundaries, cost settings and what is not provisioned. Update it with infrastructure changes.

Bootstrap handoff files contain identifiers/endpoints, not credentials, and default to ignored `src\Factory\artifacts\bootstrap`. Secret values are entered only at the local hidden prompt, never in commands or committed files. No Azure CLI login is required by the configured local application.

## Current scope

| Location | Implemented |
|---|---|
| `Factory.Contracts` | Plant, enrollment, machine binding, registration/presence, installation-profile and configuration contracts. |
| `Factory.Plant.Service` | Entra bearer API, separate browser sign-in, scoped authorization, durable background directory provisioning, Cosmos persistence and Razor inventory. |
| `Factory.Workbench` | Protected configuration/binding, resumable enrollment, machine CNG certificates, MSAL application tokens, durable reports and runtime state. |
| `Factory.Workbench.Setup` | Configuration and Windows Service maintenance, identity-preserving updates, and full owned-local removal with best-effort retirement. |
| `Factory.Workbench.Installer` / `Factory.Workbench.Package` | Graphical enrollment/service setup and a machine-wide Windows MSI with Installed Apps/start-menu integration. |
| `Factory.Workbench.Host` | Boot-time Windows Service, certificate-authenticated activation/registration/presence, and foreground diagnostics. No WSL or assignment execution. |
| `Factory.Tests` | Isolated HTTP, authorization, transactions, OIDC/cookies/forms, serialization, OpenAPI and local workbench setup checks. |
| `api` | [Contract for plant, enrollment and workbench REST operations](api/README.md). |
| `bootstrap` / `deploy` | Tiered setup scripts and shared/environment-specific Bicep. |
| `setup` | Workbench development-package publication and Windows setup entry point. |

The website lists, creates and displays plants and shows workbench inventory. Plant creation atomically records ownership, idempotency and audit data. REST authentication remains bearer-only; browser cookies never authenticate `/v1`.

Assignment execution, group resolution, automatic certificate rotation, and signed production packaging are not implemented. Enrollment is disabled unless an administrator configures the explicit provisioning identity. There is no runtime seed, demonstration identity, interactive-credential fallback or local database substitute. The existing `src\Workbench` projects remain unchanged.

`GET /health/live` proves process liveness only, not Azure connectivity or successful authentication.

## Windows installer and registration

Track the current live deployment prerequisites, installer handoff and acceptance results in the [workbench installation test working document](../../docs/workbench-installation-test.md).

The normal device installation entry point is the **MSI**, not PowerShell. In `0.1.4`, Finish continues directly into guided sign-in, enrollment and service-account verification; there is no optional enrollment checkbox. The MSI page describes software installation as an intermediate phase, not enrollment success. Sign in with an explicitly assigned `Factory.EnrollmentAdministrator` account; interrupted enrollment resumes through the Start-menu setup entry. First installation requires full interactive UI, not `/qn` or passive software-only installation. Enrollment is service-wide: the device starts **unassigned**, can report presence without belonging to a plant, and retains its identity independently of plant membership. Plant ownership or `Workbench.Registrar` alone does not permit enrollment.

Use **Enrollments** in the website to assign an enrolled device to one plant, move it, or unassign it. The operator needs enrollment administration and ownership/plant-scoped `Workbench.Registrar` authority over both the current and destination plants. Assignment changes retain the certificate and Entra identity; monotonic assignment versions reject stale reports, including after moving back to the same plant. Unassigned devices have no plant access. Workbench rows left in former plants are historical membership records, not additional active assignments.

Setup creates a non-exportable RSA machine key and a public certificate in `LocalMachine\My`. Only the public certificate is submitted. The service authorizes and journals enrollment, provisions a dedicated Entra application/service principal and `Workbench.Connect` grant, then verifies an actual matching application token before activation. It never uses the human token for Graph provisioning. Setup waits for the dedicated Windows Service account to authenticate and publish Factory presence before reporting connected. The runtime reads its current plant assignment from Factory and publishes plant registration/presence only when assigned. A connected machine remains `notReady` for execution.

The service administrator first completes [enrollment bootstrap](bootstrap/tenant/README.md#enable-workbench-enrollment). That produces a non-secret `workbench-profile-<environment>.json` with the HTTPS origin and tenant/API/installer IDs. End users do not copy those IDs. Distribute the profile only as part of your trusted installer; setup does not discover authentication authority from an arbitrary website. The HTTPS certificate must be trusted by the machine/service account, not only the enrolling user's browser. Never disable TLS validation for local development.

Developers build the **unsigned development MSI** on Windows x64 with .NET 10:

```powershell
.\src\Factory\setup\Build-WorkbenchInstaller.ps1 `
    -ReleaseId dev-001 -Version 0.1.0 `
    -ProfilePath .\src\Factory\artifacts\bootstrap\workbench-profile-development.json `
    -AllowUnsignedPackage
```

The build pins WiX SDK/extensions to 4.0.6 (MS-RL) and publishes self-contained tools/runtime plus an embedded lifecycle helper. It does not install the result. MSI versions increase as three numeric components; never reuse a release ID for different bits. `-SkipMsiValidation` is an explicit build-only option for environments where Windows Installer ICE validation cannot run; it does not constitute a passed installer acceptance check.

MSI owns `%ProgramFiles%\Factory\Workbench Setup` (tools and source payload); lifecycle setup owns `%ProgramFiles%\Factory\Workbench\releases` (selected/previous runtime). Privileged installed helper execution requires a protected location beneath 64-bit Program Files, without filesystem links or untrusted write access. A newer MSI updates an existing runtime while preserving identity, membership, configuration, journals and intentional stop/run intent. Ordinary rollback restores the prior selection. A removal intent blocks restart/re-enrollment, and rollback must not undo remote revocation or restart that device.

Do not run separate graphical or CLI maintenance while an MSI install, update, repair, or uninstall is in progress.

Interrupted enrollment resumes the original journal/operation/key. Lost directory-create responses are reconciled by tracked marker, ownership and public key; the service will not blindly create another identity. `needsAttention` requires administrator reconciliation. After correcting its cause, the original enrollment administrator can repeat guided enrollment: a fresh challenge and signature over the unchanged draft authorize reconciliation of that same operation. The service checks for an existing owned principal first; another principal attempt requires the same owned application and public certificate. An uncertain application create is never blindly repeated. Do not delete the journal or submit a replacement operation to work around it. Outstanding registration/presence reports retain their idempotency keys across restart and are resolved before fresh reports.

**Unassign is not retire.** Unassign through the website to remove plant membership while keeping the device enrolled. **Retire** through Enrollments to revoke the device across Factory, including with previously issued tokens or while offline, then delete only its recorded, provisioner-owned dedicated service principal/application. Retirement preserves audit history and waits out provisioning leases before reconciling uncertain creates. Directory failure leaves access revoked and reports retry/reconciliation status; it never reports the identity retired merely because a create could not be found.

For permanent local removal, use Installed Apps, the MSI, or setup's **Uninstall workbench...** action. Full UI offers diagnostic export before removal. Uninstall stops/removes the owned service, attempts certificate-authenticated retirement of only this enrollment with a 20-second request deadline, then removes owned releases, machine certificate/private key, configuration, installation identity, bindings, journals and logs. Network/authentication/retirement failure does not block otherwise safe local removal. The MSI log and full-UI summary distinguish confirmed, pending, attention-required, unconfirmed and no-locally-recorded-enrollment outcomes. A received retirement request continues server-side without local credentials.

Unsubmitted/unbound enrollment or a missing credential may require administrative retirement through **Enrollments**. Never interpret a lost response, stale presence or local key deletion as confirmed revocation. A completed full uninstall followed by a fresh install creates a new installation/enrollment, initially unassigned. Shared prerequisites, independently owned files and shared Factory parent directories are not purged; unknown state/release artifacts, unsafe process ownership or cleanup failure stop removal with an explicit fault. The older `0.1.3` retirement-gated cleanup remains a legacy flow, not the `0.1.4` MSI contract.

**Diagnostics:** Windows Installer verbose logging is enabled by default; its path is recorded in `HKLM\Software\Factory\WorkbenchInstaller\LastMsiLogFile` while installed. An MSI failure dialog offers **Open MSI log** and **Open setup logs**. Persistent setup traces are under `%ProgramData%\Factory\Workbench\logs\setup-<session>.jsonl`, bounded to 32 files of 256 KiB. State-directory browsing and new diagnostic traces have read-only Users access; directory-read permissions do not inherit into credential/configuration files. Do not grant an individual account FullControl over state to browse logs. Setup offers **Open setup logs**, **Open MSI log** and **Save diagnostics...**, including after enrollment failure and before uninstall. Traces record stages, versions, exception types/codes, authentication error codes, enrollment IDs and correlation IDs, not exception messages or tokens. Export includes structured setup traces, a bounded/redacted MSI log and non-secret summary; it excludes configuration, certificates/private keys, token caches and raw runtime exception logs. Save outside owned installation/state directories. Explicit user exports and Windows-managed installer/event logs survive removal; installation-owned setup logs do not.

Missing state parents are created with protected ACLs, not inherited shared-data write permissions. Existing unsafe state still requires explicit reconciliation. For a verified failed fresh installation with only pre-enrollment logs/locks, [Repair-WorkbenchDiagnosticState.ps1](setup/Repair-WorkbenchDiagnosticState.ps1) supports `-WhatIf` and confirmation; it refuses existing MSI registrations/services, enrollment/configuration, unknown entries, links or other shared-parent contents. It does not delete state or remove credentials. For `ValidateHelper`, MSI-visible helper exit codes identify prelogging failures: `40` startup, `41` state protection, `42` diagnostic initialization, `43` helper validation.

`0.1.4` adds `POST /v1/enrollments/{id}/retire-self` under the existing `Workbench.Connect` role and exact device identity binding. It requires no new Graph grants or enrollment-administrator role for the device. Deploying that service change is separate from building the MSI; until then the old service rejects the route and uninstall reports retirement unconfirmed. See the [handoff](../../docs/workbench-installation-test.md) before live acceptance.

**Remaining acceptance gates:** production code signing, automatic certificate rotation/authorized key recovery, actual Entra/Graph provisioning, real SCM install/upgrade/uninstall rollback, reboot without logon, and service-owned WSL/worker execution. Certificates currently expire after 180 days; token renewal is automatic, certificate renewal is not. Do not deploy this development slice as an indefinitely self-sustaining production workbench.

## Developer lifecycle tools

These lower-level tools remain available for local development and automation. Requires PowerShell 7 on Windows x64; building additionally requires the .NET 10 SDK. Setup and runtime are separate self-contained executables. Service lifecycle changes require an elevated terminal. Use the graphical installer above for normal sign-in/enrollment.

From the repository root, publish into a fresh output directory, configure, then install your explicitly trusted local development package:

```powershell
.\src\Factory\setup\Publish-Workbench.ps1 -ReleaseId local-001
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Configure
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Install -AllowUnsignedPackage
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Inspect
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Run
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Status
.\src\Factory\artifacts\workbench\Setup-Workbench.ps1 -Action Stop
```

The package contains `setup\Factory.Workbench.Setup.exe`, `host\Factory.Workbench.Host.exe`, the setup script, and a host file-digest manifest. Use the packaged setup tool for maintenance; the installed runtime does not modify its own binaries or service registration.

`Configure` prompts for the plant HTTPS origin and expected tenant/API/installer client IDs from a trusted installation profile, future WSL distribution/user and workspace path, repository allowlist, and local permission limits. WSL selections are stored but neither invoked nor verified in this slice. It shows the non-secret configuration and asks before saving. No credentials, plant binding, or admission are created. Shell execution and publication default to denied; an empty repository allowlist denies all repositories. Enter `-` to clear an existing allowlist. Stop the host before reconfiguration.

State defaults to `%ProgramData%\Factory\Workbench`; use `-StateDirectory` consistently with developer tools to select another installation. The MSI/graphical setup use the default machine root. Configuration, installation metadata, enrollment and binding are administrator-owned and read-only to the service; only the `runtime` child directory (status, locks, reports and failure log) is service-writable. Binaries default to `%ProgramFiles%\Factory\Workbench\releases\<releaseId>`; `-InstallDirectory` overrides the binary root on **Install only**, and later maintenance uses the saved location. These locations replace the prototype's per-user layout; existing per-user configuration is not silently migrated. Configuration writes are atomic and retain the same installation ID. Once enrollment begins, setup cannot replace the origin or authentication profile.

Install registers `Factory.Workbench.<installation-id>` with a matching dedicated virtual service account, protected state-directory/key access, automatic boot startup, and three delayed crash restarts before stopping further retries. The account is **not a validated WSL account choice**. CLI install does not start the host: `Run` records persistent run intent and starts the service. `Stop` clears that intent, so reboot does not undo an intentional stop. An enrolled service reconnects and reports presence without human sign-in; an unenrolled service explicitly reports incomplete enrollment. Neither starts WSL or claims work.

`Update` uses the new package's setup script with `-Action Update -AllowUnsignedPackage`. It stages a new immutable release, stops the service, obtains exclusive runtime ownership, records pending activation, switches the service executable, and restores prior run intent. Failed activation attempts restore the previous selection; `Rollback` explicitly selects the previous retained release. Neither action replaces configuration or journals. The current runtime has no assignments to drain; workload drain/cancellation semantics must be added with execution.

`Recover` repairs interrupted installation/service registration or conservatively restores the last committed release after interrupted activation, then honors saved run intent. Interrupted uninstall is completed instead of restarting the runtime. Incomplete maintenance blocks runtime startup until recovered; this is not yet a privileged automatic maintenance-recovery agent. Runtime crash restart and maintenance recovery are separate paths. Recovery never re-enrolls, deletes journals, or replays work.

Developer `Uninstall` performs best-effort self-retirement and full owned-local cleanup, including the installation identity. For an MSI-managed default root it refuses to bypass Windows Installer; use Installed Apps instead. The lower-level service-removal primitives and legacy retirement journal remain for compatibility/recovery, not a second user-facing purge step. WSL distributions/shared prerequisites are not installed or removed by this connection-only slice.

`Inspect` is read-only and validates configuration and selected-release digests. It explicitly reports deferred WSL/authentication/execution readiness; exit `1` means failure, and `2` means incomplete readiness. `Status` reports persisted installation state and actual SCM running state; persisted runtime observations are not proof that a process is alive. The selected installed host supports `run --root <state-directory>` for foreground diagnostics after stopping the service; exclusive ownership prevents a second host.

Copied host files are checked against SHA-256 digests, but development packages and MSI are unsigned. CLI install/update require explicit unsigned-package consent. Do not run packages from untrusted sources or treat them as production-ready. The website remains read-only for workbench registration.

## Build and checks

```powershell
dotnet test .\src\Factory\Factory.slnx
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\Bootstrap.Tests.ps1
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\EnrollmentBootstrap.Tests.ps1
```

The bootstrap checks mock Azure CLI, dotnet and secret prompts; they do not provision resources or read real user-secrets.

For an unconfigured public landing-page preview only:

```powershell
dotnet run --project .\src\Factory\Factory.Plant.Service --no-launch-profile -- --urls http://localhost:5080
```

That preview does not enable sign-in or data access. Use the development guide for the working local site.
