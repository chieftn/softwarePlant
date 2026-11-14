# Live Workbench Installation Test

Last updated: November 13, 2026 (Pacific).

**Status: service deployed; MSI 0.1.3 available for the first installation test.** Do not install MSI `0.1.2`: it predates enrollment inventory and removal. The new global API/UI is deployed and the approved operator has the dedicated administrator role. The replacement unsigned MSI passed static inspection. **No machine installation, enrollment, certificate cleanup or reboot acceptance has passed.**

This document tracks the current `src\Factory` MSI and plant-service implementation, not the older Foundry connection process in [the rev1 runbook](installation.md).

## Goal and ownership

Deliver an MSI that enrolls the tester's device into Factory inventory, initially unassigned. Assign it to the existing plant through the website, prove reconnect after reboot without human sign-in, and prove that unassign, retirement, local cleanup and re-enrollment are distinct, reversible where intended operations.

The assistant prepares the service-side enrollment changes, authentication configuration and MSI after approval. The tester runs the installer and performs the machine-side acceptance steps. A tenant administrator must approve the provisioning permissions below; the tester should not have to perform manual application registrations or copy authentication IDs.

**Next action (tester):** copy the MSI below to the test machine. Sign out/in to renew application roles, enroll the device without choosing a plant, then use **Enrollments** on the website to assign it to the existing plant. Follow the acceptance table; report the first failure rather than deleting state or creating replacement identities.

## Agreed device lifecycle

| Action | Result |
|---|---|
| Enroll in setup | Create one device identity in service-wide inventory, initially unassigned. Requires `Factory.EnrollmentAdministrator`, not plant ownership alone. |
| Assign through Enrollments | Admit the device to one plant. Requires enrollment administration and plant registration authority. |
| Move or unassign | Change membership, retaining certificate, device identity and enrollment. A move requires authority over both plants. Assignment versions reject old reports. |
| Retire through Enrollments | Revoke all Factory/plant access, including for an offline device; asynchronously reconcile/delete only its owned dedicated directory objects. This is irreversible, not unassign. |
| Unenroll and remove service in setup | Stop service, request/resume retirement, then remove owned local service, credential and binding only after confirmed retirement. Interrupted cleanup can resume. |
| MSI uninstall | Remove software/service only, retaining enrollment, credentials and journals. Not retirement or a reset. |

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
| Enrollment lifecycle source | Implemented; isolated checks | Global inventory/UI, unassigned presence, versioned membership, retirement and resumable local cleanup. Real Graph deletion, SCM and machine certificate/key cleanup remain untested. |
| Dedicated production provisioning identity | Created and attached | `factory-prod-qm3qqqsg45-enroll`; client ID `985e52b1-364c-466c-8ef9-5edd00f12d4e`. The original data identity remains attached and selected for data/cookie access. |
| API enrollment prerequisites | Configured | V2 access tokens, delegated `Plant.Access`, application-only `Workbench.Connect`; existing role IDs and browser callbacks preserved. Website health and sign-in challenge checked afterward. |
| Public installer registration | Created and preauthorized | Client ID `c15ffcc4-7d09-4f5d-864f-9fcdec452f4e`; single-tenant public client, loopback callback, only the API's delegated `Plant.Access`, no credentials. |
| Provisioner Graph grants | Assigned and verified | Read-back confirmed all three approved application permissions on the dedicated provisioning identity. |
| Replacement MSI | Built and statically checked | `0.1.3`/`enrollment-inventory-001`; profile/manifest MSI hashes, embedded cabinet/helper and runtime payload hashes checked without installation. Unsigned; ICE intentionally skipped. |
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
- [x] Hand over the package for its first installation test after service deployment and approved role assignment. Human login and machine acceptance remain tester steps, not completed prerequisites disguised as evidence.

Build instructions: [Windows installer and registration](../src/Factory/README.md#windows-installer-and-registration).

| Delivery item | Current value |
|---|---|
| Live test MSI path | `C:\repos\softwarePlant\src\Factory\artifacts\installer\enrollment-inventory-001\msi\Factory.Workbench.0.1.3.msi` |
| Version / release | `0.1.3` / `enrollment-inventory-001` |
| Size | 119,704,045 bytes |
| SHA-256 | `59DC5492C21D7EEE26BB84502662486A53B159B1E4562F247F6A6628828FB46A` |
| Production profile and payload checked | All five profile fields, MSI profile/manifest hash records, embedded cabinet/helper and runtime manifest hashes matched |
| Signing / ICE validation | Unsigned development test build / skipped by shared-machine policy, not passed |
| Superseded package | `0.1.2` / `enrollment-live-001` remains blocked; do not install |
| Ready for the tester to run | **Yes, for the first unsigned development installation test on the tester-operated machine; installation/enrollment/removal/reboot remain unverified** |

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
| Install the supplied MSI with elevation. | Installation completes and Factory Workbench Setup is available. | Not run |
| Open setup and sign in. | The approved account passes the enrollment-administrator check. No plant selector or manual registration IDs are required. | Not run |
| Enroll and start the workbench. | Provisioning completes; setup confirms service-account authentication and Factory presence while unassigned. | Not run |
| Open Enrollments on the website. | The matching device is visible, unassigned, with fresh Factory presence. Plant ownership without the dedicated role does not expose administration. | Not run |
| Assign it to the existing plant. | Matching workbench appears with fresh plant registration/presence; enrollment, Entra IDs and certificate do not change. | Not run |
| Check the installed service. | It runs under its dedicated service account and can use its machine certificate. | Not run |
| Restart the workbench service. | The same workbench reconnects without a new enrollment, certificate or human login. | Not run |
| Reboot without an interactive sign-in. | The same service starts automatically and fresh presence returns at the plant. | Not run |
| Unassign in Enrollments, then reassign. | Device stays connected to Factory; old plant access stops; reassignment retains identity and advances assignment version. | Not run |
| Retire while the device is offline. | Access is revoked immediately; directory cleanup reaches `retired` or explicitly reports attention. Previously issued tokens cannot report afterward. | Not run |
| Resume setup and choose Unenroll and remove service. | Confirmed retirement permits owned service/certificate/key/binding cleanup; interrupted cleanup resumes without deleting unrelated state. | Not run |
| Re-enroll after completed cleanup. | Same installation can enroll with a new credential/dedicated identity, initially unassigned. Old enrollment stays retired. | Not run |

Record results and correlation/enrollment IDs in ignored test artifacts. Do not put tokens, secrets, private keys or certificate exports in this document or source control.

If enrollment reports `enrollment_disabled`, provisioning failure, `needsAttention` or `retirementNeedsAttention`, stop and investigate. Do not delete journals, create replacement identities to bypass a failure, weaken TLS validation or bypass authorization. Retirement failure retains revoked access and local credentials for recovery; only verified `retired` allows credential cleanup. Disabling new enrollment must not prevent existing-device retirement.

## What this test does not prove

Connected means registration and presence work. The current runtime advertises `plant-connection` and remains `notReady` for workload execution.

This test does not establish WSL/worker execution, product-line Copilot workloads, upgrade/rollback acceptance, production code signing, automatic certificate renewal or expired-key recovery. Machine certificates currently expire after 180 days; token renewal is automatic, certificate renewal is not.

Uninstall is not a full reset: it removes local software/service but retains identity, certificate, binding and journals. Use the explicit retirement/unenrollment workflow for permanent removal, not state deletion as a troubleshooting shortcut. Native machine cleanup, actual directory deletion and reboot behavior are acceptance items, not results of isolated tests.

## Reporting completion

- **Website working:** browser access works. This does not mean an installer is ready.
- **Ready to install:** service-side prerequisites are complete and the checked live MSI, version and checksum have been delivered.
- **Install test passed:** every acceptance row above has evidence, including reconnect after reboot without interactive sign-in.
- **Production-ready workbench:** not claimed by this development connection test.

Until the delivery and acceptance gates are met, report the remaining blocker and next owner/action instead of reporting overall success.
