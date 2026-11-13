# Tier 3: Azure development harness and local debugging

Two independent steps: provision one Azure harness, then configure each developer machine. Application runtime uses its Entra client credential, **not the developer's Azure CLI login**.

## Provision the shared Azure harness

Complete [tier 1](../tenant/README.md) first. Use PowerShell 7 and Azure CLI/Bicep. The operator needs permission to create resources and Cosmos SQL role assignments, plus provider-registration/resource-group permissions if needed.

```powershell
$tenant = '<development-tenant-id>'
$subscription = '<development-subscription-id>'
az login --tenant $tenant

.\src\Factory\bootstrap\development\Deploy-Development.ps1 `
    -SubscriptionId $subscription -ResourceGroup 'factory-dev' `
    -Location 'westus2' -NamePrefix 'factory' -MaxThroughput 1000
```

The script reads `artifacts\bootstrap\tenant-development.json`, checks its tenant against Azure, creates/reuses the group, and deploys [`cosmos.nonproduction.bicep`](../../deploy/cosmos.nonproduction.bicep). It grants the **application service principal**, not your browser user, database-scoped Cosmos Data Contributor.

**Development allows all network sources by default, but always requires Entra/RBAC; account keys are disabled.** To restrict sources, provide all approved egress IPv4 addresses/CIDRs with `-AllowedIpAddresses @('203.0.113.10', '198.51.100.0/24')`. Those documentation addresses are examples, not real allowlist entries. Do not infer the full allowlist from one IP lookup: outbound traffic may use multiple IPs. Network changes can take up to 15 minutes.

The example's 1,000 RU/s autoscale ceiling is billable and not a cost estimate. Use `-WhatIf` for read-only input/context checks and a PowerShell action preview; it is not an ARM deployment validation.

Rerun with the original subscription/group/prefix/region to update the same account, including an account already deployed from the standalone template. Existing groups must have `application=Factory`, `environment=nonproduction` tags. The script refuses unrelated groups and mismatched prior metadata. Keep development separate from production.

## Configure each developer machine

Requires the **.NET 10 SDK**, PowerShell 7, and:

- `tenant-development.json` from tier 1.
- `development.json` from the harness deployment.
- The development app's client secret **Value**, obtained through the approved secret-sharing mechanism.
- `Factory.User` and, for creation, `Factory.PlantCreator` assigned to the developer in Entra.

The default files are under `src\Factory\artifacts\bootstrap`. Share only these non-secret handoff files with other developers; they do not need deployment privileges or Azure CLI installed to run this local step. To use files elsewhere, pass `-TenantConfiguration '<path>' -DevelopmentConfiguration '<path>'`.

```powershell
.\src\Factory\bootstrap\development\Set-LocalConfiguration.ps1
```

Accept the standard .NET HTTPS certificate trust prompt, then enter the Entra secret **Value**, not its Secret ID, at the hidden prompt. The script writes the six tenant/client/secret/Cosmos settings through JSON on **dotnet user-secrets stdin**. The secret never becomes a command argument or temporary file. User-secrets itself is standard local development storage outside the repository, **not encrypted vault storage**.

This does not create an alternate config provider, SQLite database, authentication bypass or custom key store. Development uses ASP.NET's default local Data Protection. No Blob or Key Vault is needed locally.

## Run or debug

From the repository root:

```powershell
dotnet run --project .\src\Factory\Factory.Plant.Service --launch-profile https
```

Open **https://localhost:7443/plants**, sign in, list plants and create one. In Visual Studio or VS Code C# Dev Kit, select the service project's `https` launch profile to debug the same configuration. Stop an old instance yourself before starting another.

The checked-in profile selects `Development`, explicit `ClientSecret` Cosmos authentication, browser sign-in and protocol/schema version `1`. The confidential web app and local Cosmos credential use the same development app registration. Production does not use this client-secret data credential.

## Repeat setup and failures

Other developers consume the two metadata files and run only local configuration. An administrator can add their user roles through the tenant script's `-UserObjectId` option or the Entra portal. There is no need to recreate the harness or reset the application's credentials.

For credential rotation, create a replacement secret without deleting the old one, rerun local configuration on affected machines, and verify sign-in **and plant access** before retiring the old credential.

| Symptom | Action |
|---|---|
| Missing/incomplete/mismatched metadata | Finish the corresponding tier or supply both matching development files. The script rejects production handoffs before prompting for secrets. |
| Entra sign-in or role error | Check the application/client ID, both callbacks, secret value/expiry and user role assignments. Sign out/in after assignment changes. |
| Generic temporary-unavailable page | Read the ASP.NET server exception/correlation ID; do not paste secret values, tokens or all user-secrets into diagnostics. |
| Cosmos authorization/network error | Verify the application's principal grant and account network policy; allow propagation after changes. Browser user roles do not grant the service data access. |

`/health/live` and an anonymous Cosmos `401` do not prove authenticated data access. Listing and creating a plant exercise the actual local service/Entra/Cosmos path.
