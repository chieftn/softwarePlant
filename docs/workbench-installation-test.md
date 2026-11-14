# Live Workbench Installation Test

Last updated: November 14, 2026 (Pacific).

**Status: unsigned MSI 0.1.4 built; matching service deployment and native acceptance pending.** The global enrollment API/UI is deployed and the approved operator has the dedicated administrator role, but the new device-self-retirement endpoint is not deployed. MSI `0.1.2`, `0.1.3` and the earlier `0.1.4` prototypes are superseded. **No machine installation, enrollment, certificate cleanup, upgrade or reboot acceptance has passed.**

**Lifecycle gate:** MSI `0.1.4` implements the [RFC 001 contract](../rfcs/001-workbench-workloads-and-installation.md#agreed-install-upgrade-and-uninstall-contract): guided enrollment, identity-preserving upgrades and complete owned-local uninstall with best-effort remote retirement. It adds automatic MSI logs, protected setup diagnostics and export before removal. Approve/deploy the matching service package before live self-retirement acceptance. No deployment or shared-machine installation is authorized by this build.

This document tracks the current `src\Factory` MSI and plant-service implementation, not the older Foundry connection process in [the rev1 runbook](installation.md).

## Goal and ownership

Deliver an MSI that enrolls the tester's device into Factory inventory, initially unassigned. Assign it to the existing plant through the website, prove reconnect after reboot without human sign-in, preserve identity through upgrades, and prove full owned-local removal with a separately reported retirement outcome. Unassign, retirement and uninstall remain distinct operations.

The assistant prepares the service-side enrollment changes, authentication configuration and MSI after approval. The tester runs the installer and performs the machine-side acceptance steps. A tenant administrator must approve the provisioning permissions below; the tester should not have to perform manual application registrations or copy authentication IDs.

**Next action (operator):** obtain deployment approval, deploy the matching service package and verify health, browser authentication and the new route before releasing this candidate for live lifecycle acceptance. **Then the tester** copies the MSI below to an isolated test machine, renews operator sign-in and follows the acceptance table. Report the first failure rather than deleting state or creating replacement identities.

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
| Enrollment lifecycle source | Implemented; focused checks | Guided setup, identity-preserving maintenance, exact-device self-retirement and full owned-local cleanup with distinct remote outcomes. 76 focused tests passed. Real Graph deletion, SCM and machine certificate/key cleanup remain untested. |
| Dedicated production provisioning identity | Created and attached | `factory-prod-qm3qqqsg45-enroll`; client ID `985e52b1-364c-466c-8ef9-5edd00f12d4e`. The original data identity remains attached and selected for data/cookie access. |
| API enrollment prerequisites | Configured | V2 access tokens, delegated `Plant.Access`, application-only `Workbench.Connect`; existing role IDs and browser callbacks preserved. Website health and sign-in challenge checked afterward. |
| Public installer registration | Created and preauthorized | Client ID `c15ffcc4-7d09-4f5d-864f-9fcdec452f4e`; single-tenant public client, loopback callback, only the API's delegated `Plant.Access`, no credentials. |
| Provisioner Graph grants | Assigned and verified | Read-back confirmed all three approved application permissions on the dedicated provisioning identity. |
| Replacement MSI | Built and statically checked | `0.1.4` / `workbench-0.1.4-004`; x64, automatic logging, registry metadata, embedded helper and all 514 image files checked across two cabinets without installation. Unsigned; ICE intentionally skipped. |
| Matching self-retirement service | Packaged, not deployed | Self-contained Linux x64 `0.1.4` ZIP below. Existing Graph grants suffice; the device receives no new administrator role. |
| Actual installation, enrollment, service startup and reboot | Not tested | These are the acceptance steps, not completed milestones. |

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
- [ ] Obtain explicit deployment approval for the `0.1.4` self-retirement service, deploy it, and verify health, browser authentication and route protection. This build did not deploy it.
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
- [ ] Release the candidate for live lifecycle acceptance after approved service deployment and isolated-machine selection. Human login and machine acceptance remain tester steps, not completed prerequisites disguised as evidence.

Build instructions: [Windows installer and registration](../src/Factory/README.md#windows-installer-and-registration).

| Delivery item | Current value |
|---|---|
| Candidate MSI path | `C:\repos\softwarePlant\src\Factory\artifacts\installer\lifecycle-014-004\msi\en-US\Factory.Workbench.0.1.4.msi` |
| Version / runtime release | `0.1.4` / `workbench-0.1.4-004` |
| Size | 170,925,721 bytes |
| SHA-256 | `51AE6CFDEB6C222D313543B2A392A850CC5FB74D82FEF690FD4BAE517DA60BEF` |
| Production profile and payload checked | Production profile retained; all 514 image file hashes, two embedded cabinets, helper hash, action sequencing/upgrade guards, x64 platform and automatic logging checked without MSI execution |
| Signing / ICE validation | Unsigned development test build / skipped by shared-machine policy, not passed |
| Superseded packages | `0.1.2`, `0.1.3`, and `lifecycle-014-001` through `lifecycle-014-003`; do not use for this acceptance |
| Matching service ZIP | `C:\repos\softwarePlant\src\Factory\artifacts\service\lifecycle-014-001\Factory.Plant.Service.zip` |
| Service ZIP size / SHA-256 | 58,021,638 bytes / `BA75C84F3D7E8B56DC4438DED94E741B8B3AB6E6C0A89088AC3E4C25D61121BE` |
| Ready for live lifecycle acceptance | **Pending approved deployment of the matching service and tester-operated machine acceptance. Without it, uninstall may report unconfirmed retirement; do not interpret local cleanup as revoked remote access.** |

MSI verbose logging is automatic. Setup writes protected, bounded JSONL traces under `%ProgramData%\Factory\Workbench\logs`. Setup offers **Open setup logs**, **Open MSI log** and **Save diagnostics**; native MSI failure UI exposes logs even if setup never starts. The uninstall flow offers export before purging local setup logs. Exported diagnostics omit credentials/configuration and redact supported MSI credential fields; inspect before sharing. Explicit diagnostic exports and Windows-managed MSI/event logs survive uninstall.

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
| Install the supplied MSI with elevation. | Software phase completes and launches mandatory guided setup; no optional enrollment checkbox. MSI verbose log exists. | Not run |
| Complete the guided sign-in/enrollment. | The approved account passes the administrator check; provisioning completes and setup verifies actual service-account authentication and fresh unassigned Factory presence. No plant selector or manual registration IDs. Cancellation/failure is not enrollment success. | Not run |
| Exercise failure diagnostics on the test machine. | Native MSI failure and enrollment/network failures expose logs; setup traces persist safely and exported ZIP excludes tokens, keys and configuration. | Not run |
| Open Enrollments on the website. | The matching device is visible, unassigned, with fresh Factory presence. Plant ownership without the dedicated role does not expose administration. | Not run |
| Assign it to the existing plant. | Matching workbench appears with fresh plant registration/presence; enrollment, Entra IDs and certificate do not change. | Not run |
| Check the installed service. | It runs under its dedicated service account and can use its machine certificate. | Not run |
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
