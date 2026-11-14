# Live Workbench Installation Test

Last updated: November 13, 2026 (Pacific).

**Status: ready for the tester's installation test.** The three approved Graph grants are assigned and verified, live enrollment is enabled, and MSI `0.1.2` contains the checked production profile. This is an unsigned development test package; ICE validation was skipped, not passed. No machine installation, enrollment or reboot acceptance has passed.

This document tracks the current `src\Factory` MSI and plant-service implementation, not the older Foundry connection process in [the rev1 runbook](installation.md).

## Goal and ownership

Deliver an MSI that the tester can run to join the existing plant, then prove that the installed Windows service reconnects after reboot without human sign-in.

The assistant prepares the service-side enrollment changes, authentication configuration and MSI after approval. The tester runs the installer and performs the machine-side acceptance steps. A tenant administrator must approve the provisioning permissions below; the tester should not have to perform manual application registrations or copy authentication IDs.

**Next action (tester):** run the MSI listed below with elevation, launch Factory Workbench Setup, sign in with the account that created the plant, and select that existing plant. Enroll and start the workbench, then follow the acceptance steps below. No further Azure resource creation or manual authentication-ID entry is required for this connection test.

## Current state

| Item | Status | Evidence or remaining work |
|---|---|---|
| Production website and browser authentication | Working | Live sign-in challenge and protected-cookie access were checked before and after an app restart. |
| Plant creation | Complete | The tester confirmed creating the plant. Do not create a replacement. Record its name/ID before the test. |
| Website user roles | Assigned | Five principals were confirmed to have both `Factory.User` and `Factory.PlantCreator`. |
| Authority to enroll into this plant | Use the account that created it | Plant creation records ownership. Setup must still authorize that account against the selected plant before provisioning. Website roles alone do not authorize enrollment into every plant. |
| Live workbench enrollment | Enabled | Dedicated managed-identity provisioning configured. Live health returned 200, browser sign-in challenged with 302 and the canonical callback, and anonymous API access returned 401. Actual authenticated machine provisioning is not yet tested. |
| Dedicated production provisioning identity | Created and attached | `factory-prod-qm3qqqsg45-enroll`; client ID `985e52b1-364c-466c-8ef9-5edd00f12d4e`. The original data identity remains attached and selected for data/cookie access. |
| API enrollment prerequisites | Configured | V2 access tokens, delegated `Plant.Access`, application-only `Workbench.Connect`; existing role IDs and browser callbacks preserved. Website health and sign-in challenge checked afterward. |
| Public installer registration | Created and preauthorized | Client ID `c15ffcc4-7d09-4f5d-864f-9fcdec452f4e`; single-tenant public client, loopback callback, only the API's delegated `Plant.Access`, no credentials. |
| Provisioner Graph grants | Assigned and verified | Read-back confirmed all three approved application permissions on the dedicated provisioning identity. |
| MSI configured for this live service | Built and statically checked | `0.1.2`, release `enrollment-live-001`; production profile matched, including its MSI file-hash record. Embedded cabinet and lifecycle helper checked. Unsigned; ICE validation skipped by shared-machine policy. Do not use the older dummy-profile MSI. |
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

- [x] Reuse the tester-created plant and its owning account; select that plant in setup. Its name/ID is not recorded here yet.
- [x] Create and attach a dedicated user-assigned provisioning managed identity to the hosted service.
- [x] Configure the existing API's v2 access tokens, delegated `Plant.Access` scope and application-only `Workbench.Connect` role, preserving the working browser configuration and existing assignments.
- [x] Create or adopt a separate public installer registration with its loopback browser callback, delegated API access and preauthorization.
- [x] Obtain explicit approval for the provisioning identity's three Graph grants.
- [x] Apply those approved grants using an authorized Entra administrator and verify them.
- [x] Apply the explicit installer/provisioner IDs and managed-identity enrollment configuration to the service.
- [x] Enable enrollment and verify the service remains healthy without breaking browser sign-in.
- [x] Produce the non-secret production installation profile.

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

If all three already exist, the script reports that no changes are needed. A partial failure can be resumed with the same file. Grant read-back, resuming the existing installer/bootstrap journal, service enablement and the live MSI build are now complete. The admin step alone does not establish machine enrollment.

The installer and workbench daemon receive no Graph provisioning permissions. The production provisioner uses managed identity, not a copied client secret or the enrolling user's token.

Use the [enrollment bootstrap instructions](../src/Factory/bootstrap/tenant/README.md#enable-workbench-enrollment) for the supported configuration path. Preview the changes first; do not blindly rerun a broader tenant bootstrap over the working website.

### 2. Build and hand over the live test package

Owner: assistant/operator.

- [x] Build a fresh MSI using the production installation profile, not the dummy profile.
- [x] Use a new release/version rather than replacing different bits under an existing release ID.
- [x] Check that the packaged origin, tenant, API and installer registration match the target environment.
- [x] Record the exact MSI path, version and SHA-256 checksum.
- [x] Clearly identify the package as an **unsigned development test build**. Signing and production acceptance are not complete.
- [x] Hand over the MSI only after all service-side prerequisites above are complete.

Build instructions: [Windows installer and registration](../src/Factory/README.md#windows-installer-and-registration).

| Delivery item | Current value |
|---|---|
| Live MSI path | `C:\repos\softwarePlant\src\Factory\artifacts\installer\enrollment-live-001\msi\Factory.Workbench.0.1.2.msi` |
| MSI version / release | `0.1.2` / `enrollment-live-001` |
| Size | 119,679,469 bytes |
| SHA-256 | `5E2B867141888E2F2A5824DB66B38F02AA387F302AA06C8719C5C689E7D771C1` |
| Production profile packaged and checked | Yes; all five non-secret fields and MSI profile file-hash record matched |
| Signing / ICE validation | Unsigned development test build / skipped by shared-machine policy, not passed |
| Ready for the tester to run | **Yes; machine acceptance remains not run** |

### 3. Confirm the test-machine prerequisites

Owner: tester.

- Windows x64 with local administrator rights.
- A browser for the initial Entra sign-in.
- Outbound HTTPS access to the plant service and Microsoft Entra endpoints; normal machine-wide HTTPS trust must work.
- The authorized Entra account for the existing plant.

The MSI carries self-contained setup/runtime tools. The tester does not need the .NET SDK, Azure CLI, manual application-registration steps or copied secrets. WSL, Git and Copilot runtime setup are not prerequisites for this connection-only test.

## Tester acceptance steps

Run these only after the package is explicitly marked ready above.

| Step | Required result | Status |
|---|---|---|
| Install the supplied MSI with elevation. | Installation completes and Factory Workbench Setup is available. | Not run |
| Open setup, sign in and select the existing plant. | The intended plant is available to the authorized account. No replacement plant is created. | Not run |
| Enroll and start the workbench. | Provisioning completes; setup confirms the Windows service account has authenticated and published presence. | Not run |
| Check the plant website. | The matching workbench is visible with fresh presence, not merely an old inventory row. | Not run |
| Check the installed service. | It runs under its dedicated service account and can use its machine certificate. | Not run |
| Restart the workbench service. | The same workbench reconnects without a new enrollment, certificate or human login. | Not run |
| Reboot without an interactive sign-in. | The same service starts automatically and fresh presence returns at the plant. | Not run |

Record results and correlation/enrollment IDs in ignored test artifacts. Do not put tokens, secrets, private keys or certificate exports in this document or source control.

If enrollment reports `enrollment_disabled`, provisioning failure or `needsAttention`, stop and investigate that failure. Do not delete the enrollment journal, repeatedly create replacement identities, weaken TLS validation or bypass authorization.

## What this test does not prove

Connected means registration and presence work. The current runtime advertises `plant-connection` and remains `notReady` for workload execution.

This test does not establish WSL/worker execution, product-line Copilot workloads, upgrade/rollback acceptance, production code signing, automatic certificate renewal or expired-key recovery. Machine certificates currently expire after 180 days; token renewal is automatic, certificate renewal is not.

Uninstall is not a full reset: it removes the local service/runtime but retains identity, certificate, binding and journals, and does not revoke the remote Entra identity. Any later cleanup needs an explicit plan; do not use state deletion as a troubleshooting shortcut.

## Reporting completion

- **Website working:** browser access works. This does not mean an installer is ready.
- **Ready to install:** service-side prerequisites are complete and the checked live MSI, version and checksum have been delivered.
- **Install test passed:** every acceptance row above has evidence, including reconnect after reboot without interactive sign-in.
- **Production-ready workbench:** not claimed by this development connection test.

Until the delivery and acceptance gates are met, report the remaining blocker and next owner/action instead of reporting overall success.
