# Tier 1: Entra tenant

Run once for a development environment, then rerun to add users or reconcile the registration. This tier changes **Entra**, not Azure resource groups. Use PowerShell 7 and Azure CLI, signed into the intended tenant:

```powershell
$tenant = '<tenant-id>'
az login --tenant $tenant
```

The operator needs permission to create/manage the application and enterprise application. Assigning app roles to users additionally needs directory assignment privileges (for example, Cloud Application Administrator), not merely Azure subscription Contributor. `Initialize-Tenant.ps1` does not grant itself permissions or request Graph application permissions. The separate enrollment bootstrap below explicitly grants Graph permissions to a dedicated provisioner. Permission failures stop the script; have the appropriate tenant administrator perform the failing step or rerun it.

## Create or adopt the development registration

For a new registration:

```powershell
.\src\Factory\bootstrap\tenant\Initialize-Tenant.ps1 `
    -TenantId $tenant -AssignCurrentUser
```

**For the existing working FactoryPlant registration**, supply its application/client ID so it is reused:

```powershell
.\src\Factory\bootstrap\tenant\Initialize-Tenant.ps1 `
    -TenantId $tenant -ClientId '<existing-development-client-id>' -AssignCurrentUser
```

The script configures:

| Object | Configuration |
|---|---|
| Application | Single tenant; API access-token version 2; `api://<client-id>` identifier; delegated `Plant.Access` scope. |
| Web sign-in | Authorization-code callbacks `https://localhost:7443/signin-oidc` and `https://localhost:7443/signout-callback-oidc`; implicit grants disabled. |
| App roles | `Factory.User` and `Factory.PlantCreator` for users; `Workbench.Connect` for applications. Creating that daemon role does not enroll or authorize a workbench. |
| Enterprise application | Service principal, with user assignment required. |
| Requested user | `-AssignCurrentUser`, or `-UserObjectId '<tenant-user-object-id>'`, assigns both user roles. Omit both switches to leave assignments to an administrator. |

User role assignments govern browser access. They do **not** give browser users direct Cosmos access. The development ASP.NET application receives its own Cosmos permission in tier 3.

If assignment is done manually: **Entra admin center > Enterprise applications > the application > Users and groups > Add user/group**. Assign `Factory.User`, then `Factory.PlantCreator` for users allowed to create plants. Start a new browser sign-in after changes.

## Client secret: one manual step

No script creates, resets or rotates application credentials.

1. In the intended Entra tenant, open **App registrations > your application > Certificates & secrets > New client secret**.
2. Choose the expiration under your credential policy. Copy its **Value**, not its Secret ID, into your password manager; the value is only shown once. A still-valid existing secret can be reused.
3. Keep the value out of chat, command arguments, output files and source control. The local configuration script in [tier 3](../development/README.md#configure-each-developer-machine) prompts for it without echoing it.

If you cannot manage credentials, an application owner/administrator must create and deliver the value through your approved secret-sharing mechanism. Adding another credential need not invalidate existing credentials; do not reset all credentials to bootstrap a second developer.

## Reruns, handoff and production

The default handoff is `src\Factory\artifacts\bootstrap\tenant-development.json`. It records tenant, application and service-principal identifiers and callback origin, **not secrets or user identities**. Keep it for subsequent tiers.

Reruns use that file, retain role/scope IDs and unrelated registration settings, and avoid duplicate assignments. If interrupted after application creation, the file is marked incomplete; rerun the same command to finish. An existing display name is never sufficient for automatic adoption: provide `-ClientId`. Use a separate `-OutputPath` for another tenant/environment. Do not delete the file and blindly recreate registrations.

`-WhatIf` performs read-only context/discovery checks and skips mutations and file writes. Login is always explicit; a wrong tenant fails rather than silently switching accounts.

The **current production backend needs no app registration or client secret**: its Cosmos access uses a managed identity. When production website hosting is implemented, use a separate registration:

```powershell
.\src\Factory\bootstrap\tenant\Initialize-Tenant.ps1 `
    -TenantId $tenant -Environment production `
    -PublicOrigin 'https://<production-hostname>'
```

That prepares only Entra metadata (`tenant-production.json`), not a hosted website, production credential store or cookie key ring.

## Enable workbench enrollment

This is a separate, privileged **one-time service-administrator step**, not an action required at each workbench. Normal users enroll through the MSI/graphical setup. The website remains read-only for workbench registration.

Prepare a dedicated provisioning identity first: an existing **user-assigned managed identity** attached to the hosted service for production, or a separate single-tenant application/service principal with a client secret for Development. Do not reuse the web/API or Cosmos data-access identity. Production backend bootstrap does not currently create or attach the provisioning identity.

A tenant administrator able to grant Microsoft Graph application permissions (such as Privileged Role Administrator) runs:

```powershell
.\src\Factory\bootstrap\tenant\Initialize-WorkbenchEnrollment.ps1 `
    -TenantConfiguration .\src\Factory\artifacts\bootstrap\tenant-development.json `
    -ProvisioningClientId '<dedicated-provisioner-client-id>' `
    -ProvisioningCredential ClientSecret `
    -GrantProvisioningPermissions
```

For production, select `-Environment production`, the production tenant metadata, and `-ProvisioningCredential ManagedIdentity`. The script requires the exact existing provisioner ID, supports `-WhatIf`, and prompts before changes. A rerun resumes the recorded installer identity; `-InstallerClientId` explicitly adopts an existing compatible public installer app instead of trusting a matching display name.

It creates/configures the installer public client with `http://localhost` browser redirect, delegated `Plant.Access`, and API preauthorization. It grants the provisioner Graph **`Application.ReadWrite.OwnedBy`**, **`Application.Read.All`**, and **`AppRoleAssignment.ReadWrite.All`**. The last grant is broad tenant authority: the service's fixed API/role checks do not narrow the underlying Graph permission. The installer and daemon receive no Graph grants, secrets, or human directory-administration privileges.

Outputs are ignored non-secret metadata (`enrollment-<environment>.json`), a `serviceConfiguration` object, and the five-field `workbench-profile-<environment>.json` consumed by the MSI build. No client secret is created or written to these files. Apply `serviceConfiguration` to the intended service environment. A hosted service must explicitly attach/select the provisioner managed identity and share the Data Protection key ring across instances; default/interactive credential discovery is not supported.

For Development only, create/reuse the dedicated provisioner secret through Entra Certificates & secrets, then apply enrollment settings using a hidden prompt:

```powershell
.\src\Factory\bootstrap\development\Set-EnrollmentConfiguration.ps1
```

This writes standard .NET user-secrets through stdin without changing existing web/data credentials. Restart the plant service yourself after configuration. Production rejects client-secret provisioning. An unconfigured service returns `503 enrollment_disabled`; it never seeds a machine identity or provisions using the enrolling user's token.

Build/distribute the [Windows installer](../../README.md#windows-installer-and-registration) with the generated installation profile. Local development HTTPS trust must work for the Windows Service account, not only the interactive browser. Live directory provisioning, private-key ACLs and cold boot require acceptance in an isolated environment before production use.
