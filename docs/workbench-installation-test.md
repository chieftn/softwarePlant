# Live Workbench Installation Test

Last updated: November 14, 2026 (Pacific).

**Status: tester reports successful enrollment after the servicing-ACL and service recovery corrections.** MSI `0.1.4` / `workbench-0.1.4-007` is unchanged. The tester completed the guarded failed-state repair. `006` then passed state protection and all helper-file checks but rejected the first helper-directory write ACL. The supplied test-machine Program Files ACL contains TrustedInstaller's container-only inheritance: a privileged Windows servicing writer incorrectly classified as untrusted. An in-memory regression reproduced that rejection before the correction. `007` recognizes only that exact servicing SID alongside SYSTEM and Administrators; it grants no permissions and still rejects ordinary Users and unrelated service writers, including generic write/all masks. **Do not repeat the repair. Full machine lifecycle acceptance, including reboot, upgrade and cleanup, remains outstanding.** Packages through `lifecycle-014-006` are superseded.

**Lifecycle gate:** MSI `0.1.4` implements the [RFC 001 contract](../rfcs/001-workbench-workloads-and-installation.md#agreed-install-upgrade-and-uninstall-contract): guided enrollment, identity-preserving upgrades and complete owned-local uninstall with best-effort remote retirement. It adds automatic MSI logs, protected setup diagnostics and export before removal. The matching service was subsequently deployed with explicit approval; route protection and browser challenge passed. Shared-machine installation remains unauthorized.

**Enrollment blocker resolved (tester-reported):** guided setup reached signed enrollment, created the device application with the correct provisioner owner, then Graph returned 403 during service-principal creation. The three approved grants were verified. With explicit approval, the service-only correction removes the extra owner binding from principal creation and verifies ownership before proceeding; a fresh signed submission of the unchanged draft can now reconcile `needsAttention`. It checks existing principals first and verifies the recorded application/public certificate before another principal attempt; uncertain application creation is never repeated. Graph failures retain safe error codes/request IDs, not response messages or credentials. Deployment `a4d4ef1a-366c-49a9-8994-eb51bd8a795c` reached RuntimeSuccessful at 14:33 Pacific, preserving all settings, identities, networking, auth and grants. **Directory.Read.All was explicitly declined and was not granted. The tester then reported successful enrollment.** Reboot, assignment, maintenance and cleanup still require separate acceptance.

This document tracks the current `src\Factory` MSI and plant-service implementation, not the older Foundry connection process in [the rev1 runbook](installation.md).

## Goal and ownership

Deliver an MSI that enrolls the tester's device into Factory inventory, initially unassigned. Assign it to the existing plant through the website, prove reconnect after reboot without human sign-in, preserve identity through upgrades, and prove full owned-local removal with a separately reported retirement outcome. Unassign, retirement and uninstall remain distinct operations.

The assistant prepares the service-side enrollment changes, authentication configuration and MSI after approval. The tester runs the installer and performs the machine-side acceptance steps. A tenant administrator must approve the provisioning permissions below; the tester should not have to perform manual application registrations or copy authentication IDs.

**Next action (tester):** copy the MSI below to an isolated Windows x64 test machine, run with administrator elevation, renew operator sign-in and follow the acceptance table. Service deployment is complete. Report the first failure rather than deleting state or creating replacement identities.

## Agreed device lifecycle

| Action | Result |
|---|---|
| Install and complete guided setup | Create one device identity in service-wide inventory, initially unassigned, and verify actual service-account authentication. Requires `Factory.EnrollmentAdministrator`, not plant ownership alone. Software-phase completion alone is not enrollment success. |
| Assign through Enrollments | Admit the device to one plant. Requires enrollment administration and plant registration authority. |
| Move or unassign | Change membership, retaining certificate, device identity and enrollment. A move requires authority over both plants. Assignment versions reject old reports. |
| Retire through Enrollments | Revoke all Factory/plant access, including for an offline device; asynchronously reconcile/delete only its owned dedicated directory objects. This is irreversible, not unassign. |
| Upgrade or repair | Refresh software/service while retaining identity, certificate, assignment, policy/configuration and recovery evidence. Preserve an intentional stop. Upgrade-internal removal must never retire or purge the device. |
| MSI uninstall | Offer diagnostics export, stop/remove the service, make a bounded self-retirement attempt, then purge owned credentials, configuration, journals, setup logs and software. Unknown files/links or credential-removal failures remain visible local failures. Shared prerequisites and independently owned files are not deleted. |
| Reinstall after successful full uninstall | Create a new installation/enrollment and credential, initially unassigned. Any unconfirmed old remote enrollment requires separate administrator follow-up. |

Unassigned devices still report Factory presence but cannot report to a plant. Old plant inventory rows are historical memberships, not simultaneous assignments. Remote retirement cannot stop an offline process or erase its local state; no workload execution/cancellation exists in this increment.

## Current state

| Item | Status | Evidence or remaining work |
|---|---|---|
| Production website and browser authentication | Working | Live sign-in challenge and protected-cookie access were checked before and after an app restart. |
| Plant creation | Complete | The tester confirmed creating the plant. Do not create a replacement. Record its name/ID before the test. |
| Website user roles | Assigned | Five principals were confirmed to have both `Factory.User` and `Factory.PlantCreator`. |
| Dedicated enrollment administrator | Assigned and read back | The tester explicitly approved the signed-in CLI account; only that principal received the new role. Existing role IDs, assignments and browser callbacks were preserved. Renew browser/installer sign-in. |
| Authority to assign to the existing plant | Existing owner, with the new enrollment role | Assignment checks current plant ownership/plant-scoped registrar authority. Plant creation remains complete. |
| Live workbench enrollment | Global implementation deployed | Deployment `458c6993-829f-4cee-a115-9dc7a5445c74` reached RuntimeSuccessful. Global API challenges anonymously with 401; old plant-bound enrollment route returns 404. Actual machine provisioning is not tested. |
| Enrollment lifecycle source | Implemented; focused checks | 100 focused tests passed, including inherited TrustedInstaller policy, minimal principal creation/ownership verification, signed same-operation recovery, uncertain-create protection and nonsecret Graph errors. Real Graph deletion, SCM and machine certificate/key cleanup remain untested. |
| Dedicated production provisioning identity | Created and attached | `factory-prod-qm3qqqsg45-enroll`; client ID `985e52b1-364c-466c-8ef9-5edd00f12d4e`. The original data identity remains attached and selected for data/cookie access. |
| API enrollment prerequisites | Configured | V2 access tokens, delegated `Plant.Access`, application-only `Workbench.Connect`; existing role IDs and browser callbacks preserved. Website health and sign-in challenge checked afterward. |
| Public installer registration | Created and preauthorized | Client ID `c15ffcc4-7d09-4f5d-864f-9fcdec452f4e`; single-tenant public client, loopback callback, only the API's delegated `Plant.Access`, no credentials. |
| Provisioner Graph grants | Assigned and verified | Read-back confirmed all three approved application permissions on the dedicated provisioning identity. |
| Replacement MSI | Servicing-ACL correction built and statically checked | `0.1.4` / `workbench-0.1.4-007`; embedded/installed helper match and all 514 image files checked across two cabinets without installation. Production profile, runtime release, validation actions and logging match. Unsigned; ICE intentionally skipped. |
| Matching lifecycle/recovery service | Deployed and protected-route checks passed | Latest deployment `a4d4ef1a-366c-49a9-8994-eb51bd8a795c` reached RuntimeSuccessful. Health 200, correct Entra authorization-code/PKCE challenge, inventory/submission/retire-self 401/Bearer; invalid-token submission 401. All 24 settings, both identities, Key Vault selector, subnet, route-all and three Graph grants preserved; Easy Auth remains disabled. Actual device retirement is not tested. |
| Actual installation | `004`, `005` and `006` failed | The tester completed the guarded state repair before `006`. Its copied JSONL identifies `helper-directory-write-permissions` / `UnauthorizedAccessException` after successful file and directory-owner checks. The supplied Program Files ACL and red regression establish the TrustedInstaller writer-policy bug corrected in `007`; native acceptance of `007` remains pending. The original `004` trace did not capture its cause. |
| Enrollment, service startup and reboot | Enrollment, running service and plant assignment succeeded (tester-reported) | After the service correction, the tester reported successful enrollment, a running service and assignment to the plant. Fresh plant presence and same-identity reboot/reconnect remain to be recorded. |

### Target environment

| Setting | Value |
|---|---|
| Plant service | `https://factory-prod-qm3qqqsg45-web.azurewebsites.net` |
| Resource group | `factory-prod` |
| Subscription | `ca0f0469-707d-482c-9a7b-a59fc9530d7a` |
| Entra tenant | `b1541281-df2f-4e68-9340-14b9554fbd50` |
| Existing web/API registration | `FactoryPlant` / `5b35c3b3-6112-4fbe-afa3-c0452e7a50a1` |
| Application region | West Central US |
| Existing Cosmos region | West US 2 |
| Existing plant name/ID | Not yet recorded here; creation is complete. |
| Test machine | Selected and operated by the tester. No installation on the shared development machine is authorized by this document. |

## Before handing over an installer

### 1. Prepare service-side enrollment

Owner: assistant/operator, with tenant-administrator approval.

- [x] Reuse the tester-created plant; no replacement is needed. Its name/ID is not recorded here yet. Setup no longer selects a plant.
- [x] Create and attach a dedicated user-assigned provisioning managed identity to the hosted service.
- [x] Configure the existing API's v2 access tokens, delegated `Plant.Access` scope and application-only `Workbench.Connect` role, preserving the working browser configuration and existing assignments.
- [x] Create or adopt a separate public installer registration with its loopback browser callback, delegated API access and preauthorization.
- [x] Obtain explicit approval for the provisioning identity's three Graph grants.
- [x] Apply those approved grants using an authorized Entra administrator and verify them.
- [x] Apply the explicit installer/provisioner IDs and managed-identity enrollment configuration to the service.
- [x] Enable enrollment and verify the service remains healthy without breaking browser sign-in.
- [x] Produce the non-secret production installation profile.
- [x] Approve the exact enrollment-administrator principal; add/preserve the new role and assign it only as approved.
- [x] Deploy the global enrollment/assignment/retirement implementation.
- [x] Verify health, anonymous global API protection and browser challenge with the production tenant/callback. Original canonical settings hash, both identities, Key Vault selector and subnet were preserved; Easy Auth remains disabled.
- [x] Obtain explicit deployment approval for the `0.1.4` self-retirement service, deploy it, and verify health, browser authorization-code/PKCE challenge and route protection. Deployment `598de7cf-4d8c-4ecd-a54b-8fcf39e96eaa`; verified November 14, 2026 at 11:45 Pacific. No settings/identity/network/grant changes.
- [ ] Tester: renew operator sign-in and verify authenticated inventory and existing plant access. Automated CLI token acquisition did not complete and was stopped; no extra consent was granted. This is not a completed human login.
- [ ] Tester: verify role enforcement with renewed sign-in; ownership alone must not authorize enrollment.

The provisioner requires these Microsoft Graph **application permissions**:

| Permission | Purpose |
|---|---|
| `Application.ReadWrite.OwnedBy` | Create/manage the machine applications owned by the provisioner. |
| `Application.Read.All` | Read application metadata used during provisioning and reconciliation. |
| `AppRoleAssignment.ReadWrite.All` | Assign the machine's `Workbench.Connect` application role. |

**`AppRoleAssignment.ReadWrite.All` is tenant-wide authority.** Application checks do not narrow that underlying permission. Website-authentication approval did not approve these enrollment grants. Do not grant them simply because they appear on this checklist.

The tester subsequently approved these three enrollment grants explicitly. Approval does not supply directory privileges to the CLI account. [Microsoft's grant API documentation](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0#permissions) identifies Privileged Role Administrator as the least-privileged supported role for granting Microsoft Graph application roles. Azure subscription/resource-group ownership and Application Administrator are not substitutes.

The administrator has now applied the grants to provisioner object ID `0489939c-b6c1-4325-af75-dfd2d6f274ee`, and the assistant has verified all three by read-back. The handoff below is retained for repeatable administration, not as an outstanding installation prerequisite. Do not grant a directory administrator role to the installer, workbench or Cosmos identity.

#### Exact handoff for the tenant administrator

Forward this standalone, non-secret script:

```text
src\Factory\artifacts\bootstrap\enrollment-production\Grant-ApprovedGraphPermissions.ps1
```

It needs PowerShell 7 and Azure CLI, but no repository checkout, Azure subscription access or Microsoft Graph PowerShell module. The file pins the approved tenant and provisioner IDs, checks that the target is that managed identity, adds only missing grants and verifies them. It does not activate or assign administrator roles.

The script's read-only `-WhatIf` identified the three missing grants without applying them. The administrator subsequently applied them; all three are now verified.

With Privileged Role Administrator (or Global Administrator) active, the administrator runs the following from the directory containing the copied script:

```powershell
az login --tenant b1541281-df2f-4e68-9340-14b9554fbd50 --allow-no-subscriptions
pwsh -NoProfile -File .\Grant-ApprovedGraphPermissions.ps1 -WhatIf
pwsh -NoProfile -File .\Grant-ApprovedGraphPermissions.ps1
```

The final command asks for confirmation before applying the grants. The administrator should report this final message, not send tokens or credentials:

```text
VERIFIED: all three approved Graph application permissions are assigned to the enrollment managed identity.
```

If all three already exist, the script reports that no changes are needed. A partial failure can be resumed with the same file. The earlier grant read-back/bootstrap/service enablement are complete, but do not establish deployment of the new lifecycle or successful machine enrollment. No additional Graph permissions are requested for this change.

The installer and workbench daemon receive no Graph provisioning permissions. The production provisioner uses managed identity, not a copied client secret or the enrolling user's token.

Use the [enrollment bootstrap instructions](../src/Factory/bootstrap/tenant/README.md#enable-workbench-enrollment) for the supported configuration path. Preview the changes first; do not blindly rerun a broader tenant bootstrap over the working website.

### 2. Build and hand over the live test package

Owner: assistant/operator.

- [x] Build a replacement MSI using the production installation profile, not the dummy profile.
- [x] Use a new release/version rather than replacing different bits under an existing release ID.
- [x] Check that the packaged origin, tenant, API and installer registration match the deployed service.
- [x] Record the replacement MSI path, version and SHA-256 checksum.
- [x] Clearly identify the package as an **unsigned development test build**. Signing and production acceptance are not complete.
- [x] Package the matching self-retirement service without deploying it.
- [x] Release the candidate for tester-operated lifecycle acceptance after approved service deployment. The tester selects an isolated machine; human login and machine acceptance remain tester steps, not completed prerequisites disguised as evidence.

Build instructions: [Windows installer and registration](../src/Factory/README.md#windows-installer-and-registration).

| Delivery item | Current value |
|---|---|
| Candidate MSI path | `C:\repos\softwarePlant\src\Factory\artifacts\installer\lifecycle-014-007\msi\en-US\Factory.Workbench.0.1.4.msi` |
| Version / runtime release | `0.1.4` / `workbench-0.1.4-007` |
| Size | 170,933,913 bytes |
| SHA-256 | `5336F166FECFEC54CAB9501C439909DEF71C8FAF5EE8E3A0EA4B4C070A6001D2` |
| Production profile and payload checked | Production profile retained; all 514 image file hashes, two embedded cabinets, embedded/installed helper hash and automatic logging checked without MSI execution. MSI sequencing/security guards are unchanged from the previous inspected build. |
| Signing / ICE validation | Unsigned development test build / skipped by shared-machine policy, not passed |
| Superseded packages | `0.1.2`, `0.1.3`, and `lifecycle-014-001` through `lifecycle-014-006`; do not use for this acceptance |
| Matching service ZIP | `C:\repos\softwarePlant\src\Factory\artifacts\service\lifecycle-014-002\Factory.Plant.Service.zip` |
| Service ZIP size / SHA-256 | 58,023,156 bytes / `93BAD7611DFBB6A9F4D5D7B63DCDA384271A620A5A50AB58D1BC3312874353D7` |
| Service deployment | `a4d4ef1a-366c-49a9-8994-eb51bd8a795c`, RuntimeSuccessful; verified November 14, 2026 at 14:33 Pacific. Safe evidence: `src\Factory\artifacts\service\lifecycle-014-002\deployment-result.json`. |
| Ready for tester retry | **The specific `006` writer-policy failure was reproduced and corrected in `007`. State repair is already complete; no further permissions or repair script are needed. Unsigned; full native lifecycle acceptance remains unverified.** |

MSI verbose logging is automatic. Setup writes protected, bounded JSONL traces under `%ProgramData%\Factory\Workbench\logs`. Setup offers **Open setup logs**, **Open MSI log** and **Save diagnostics**; native MSI failure UI exposes logs even if setup never starts. The uninstall flow offers export before purging local setup logs. Exported diagnostics omit credentials/configuration and redact supported MSI credential fields; inspect before sharing. Explicit diagnostic exports and Windows-managed MSI/event logs survive uninstall.

Traces identify file/directory link, ACL-read, owner and write-permission failures with exception type/code, not secret-bearing exception messages. After an error, dismiss the MSI dialog and use **Open setup logs** on the failure screen. If no new validation trace exists, read the MSI's actual helper exit code: `40` startup, `41` state protection, `42` logging initialization, `43` helper validation. Never diagnose test-machine permissions by inspecting the development machine.

### Completed failed-state repair

The tester ran the guarded script supplied beside `006` in elevated 64-bit PowerShell, previewed it and confirmed the repair:

```powershell
.\Repair-WorkbenchDiagnosticState.ps1 -WhatIf
.\Repair-WorkbenchDiagnosticState.ps1
```

The script reported successful permission repair without removing files, services, certificates or enrollments. The subsequent `006` trace confirms state protection and diagnostic initialization succeeded. **This repair is complete; do not repeat it for `007`.** The script preserves logs/locks and refuses other shared-parent contents, unknown files/links, configuration/enrollment, registered MSI installations or workbench services. Fresh machines do not require this repair script.

### 3. Confirm the test-machine prerequisites

Owner: tester.

- Windows x64 with local administrator rights.
- A browser for the initial Entra sign-in.
- Outbound HTTPS access to the plant service and Microsoft Entra endpoints; normal machine-wide HTTPS trust must work.
- An explicitly assigned Factory enrollment-administrator account, with registration authority over the existing plant for website assignment.

The MSI carries self-contained setup/runtime tools. The tester does not need the .NET SDK, Azure CLI, manual application-registration steps or copied secrets. WSL, Git and Copilot runtime setup are not prerequisites for this connection-only test.

## Tester acceptance steps

Run these only after the package is explicitly marked ready above.

| Step | Required result | Status |
|---|---|---|
| Install the supplied MSI with elevation. | Software phase completes and launches mandatory guided setup; no optional enrollment checkbox. MSI verbose log exists. | `004`/`005`/`006` failed; state repair completed; `007` pending |
| Complete the guided sign-in/enrollment. | The approved account passes the administrator check; provisioning completes and setup verifies actual service-account authentication and fresh unassigned Factory presence. No plant selector or manual registration IDs. Cancellation/failure is not enrollment success. | Tester reported successful enrollment after service correction; account-authentication/presence evidence not separately captured |
| Exercise failure diagnostics on the test machine. | Native MSI failure and enrollment/network failures expose logs; setup traces persist safely and exported ZIP excludes tokens, keys and configuration. | Not run |
| Open Enrollments on the website. | The matching device is visible, unassigned, with fresh Factory presence. Plant ownership without the dedicated role does not expose administration. | Not run |
| Assign it to the existing plant. | Matching workbench appears with fresh plant registration/presence; enrollment, Entra IDs and certificate do not change. | Assignment succeeded (tester-reported); fresh plant presence/identity evidence not separately captured |
| Check the installed service. | It runs under its dedicated service account and can use its machine certificate. | Running service confirmed by tester; account/certificate evidence not separately captured |
| Restart the workbench service. | The same workbench reconnects without a new enrollment, certificate or human login. | Not run |
| Reboot without an interactive sign-in. | The same service starts automatically and fresh presence returns at the plant. | Not run |
| Upgrade an enrolled device, then repeat with it intentionally stopped. | Identity, certificate, assignment, policy/configuration and recovery evidence survive; running device reconnects, stopped device stays stopped. Upgrade removal never requests retirement. | Not run |
| Interrupt/roll back maintenance on the test machine. | Recovery is explicit and identity is preserved; removal intent never restarts a retiring device. | Not run |
| Unassign in Enrollments, then reassign. | Device stays connected to Factory; old plant access stops; reassignment retains identity and advances assignment version. | Not run |
| Uninstall while connected, exporting diagnostics first. | Local owned service, certificate/key, configuration, journals, setup logs and software are removed. UI/MSI log report remote retirement separately; accepted retirement reconciles without the removed device. Export remains available. | Not run |
| Reinstall after completed cleanup. | New installation/enrollment and credential, initially unassigned. Old retirement is not reversed. | Not run |
| Uninstall while offline or unable to authenticate. | Safe owned-local cleanup still completes, remote outcome is explicitly unconfirmed, and an administrator can retire the old enrollment through the website. No implied durable remote queue. | Not run |
| Test interrupted cleanup and an unrelated file on the test machine. | Cleanup resumes from durable intent; credential failure/unknown files are visible, unrelated/shared artifacts are never silently purged, and the retiring service does not restart. | Not run |
| Separately retire a device through Enrollments while offline. | Factory access is revoked before directory deletion, including for previously issued tokens. Local uninstall remains a separate operation. | Not run |

Record results and correlation/enrollment IDs in ignored test artifacts. Do not put tokens, secrets, private keys or certificate exports in this document or source control.

If enrollment reports `enrollment_disabled`, provisioning failure, `needsAttention` or `retirementNeedsAttention`, stop and investigate. Do not delete journals, create replacement identities to bypass a failure, weaken TLS validation or bypass authorization. Disabling new enrollment must not prevent existing-device retirement.

Uninstall reports remote outcomes separately: **confirmed** means retired; **pending** means accepted with access revoked and deletion pending; **attention** means revoked but server cleanup needs attention; **unconfirmed** proves neither revocation nor a queued request; **not-enrolled** means no enrollment was recorded locally, not that cloud inventory was searched. Local cleanup can complete after any bounded attempt. For unconfirmed/partial enrollment, preserve the diagnostic export and use administrator inventory/retirement to reconcile the old identity.

## What this test does not prove

Connected means registration and presence work. The current runtime advertises `plant-connection` and remains `notReady` for workload execution.

Even a successful connection test does not establish WSL/worker execution, product-line Copilot workloads, production code signing, automatic certificate renewal or expired-key recovery. Upgrade/rollback and cleanup require their own acceptance rows above. Machine certificates currently expire after 180 days; token renewal is automatic, certificate renewal is not.

MSI `0.1.4` deliberately resets owned local state on uninstall; this does not guarantee remote retirement. Shared prerequisites/WSL, independently owned files, Windows-managed logs and explicit diagnostic exports are not owned-local purge targets. Native machine cleanup, actual directory deletion and reboot behavior are acceptance items, not results of isolated tests.

## Reporting completion

- **Website working:** browser access works. This does not mean an installer is ready.
- **Ready to install:** service-side prerequisites are complete and the checked live MSI, version and checksum have been delivered.
- **Install test passed:** every acceptance row above has evidence, including reconnect after reboot without interactive sign-in.
- **Production-ready workbench:** not claimed by this development connection test.

Until the delivery and acceptance gates are met, report the remaining blocker and next owner/action instead of reporting overall success.
