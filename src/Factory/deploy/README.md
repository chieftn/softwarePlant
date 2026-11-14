# Factory infrastructure inventory

Keep this inventory current whenever templates or bootstrap behavior change. It describes **what the templates provision**, not a claim that every target subscription has been deployed or runtime-verified.

## Supported bootstrap entry points

| Environment | Script | Template |
|---|---|---|
| Azure-backed local development | [`Deploy-Development.ps1`](../bootstrap/development/Deploy-Development.ps1) | `cosmos.nonproduction.bicep` |
| Production backend only | [`Deploy-Production.ps1`](../bootstrap/production/Deploy-Production.ps1) | `cosmos.production.bicep` |

Both use `cosmos-resources.bicep`. Subscriptions must already exist. Scripts require explicit subscription/resource-group/region targets; they create missing groups, register required providers and reject unrelated existing groups. They do not change the existing Workbench deployment.

Resource names are deterministic from the subscription/resource-group ID, prefix and environment. Preserve those inputs to reuse existing standalone-template resources. Different environments must use separate groups and identities. Handoff files default to ignored `src\Factory\artifacts\bootstrap`; they contain identifiers/endpoints only.

Workbench enrollment has a separate [privileged tenant bootstrap](../bootstrap/tenant/README.md#enable-workbench-enrollment). It creates the public installer registration, grants an explicitly selected existing provisioner Graph permissions, and emits service settings plus a non-secret MSI profile. It does not create Azure resources, attach managed identities to App Service, deploy the new service code, or change the data identity. Production must separately create/attach the dedicated provisioning identity; Development may explicitly select a separate confidential application. None of the backend Bicep entry points grants Graph authority.

## Production API hosting

[`service.production.bicep`](service.production.bicep) adds a Linux plan and .NET 10 App Service to an existing production backend resource group. Supply the app/plan names, location, API client ID, and existing Cosmos account, data identity and VNet names. `planSku` defaults to S1; B1, P0v3 and P1v3 are alternatives requiring explicit cost approval and regional quota checks. It references those backend resources without redeploying them, joins the existing `app-service` subnet, and attaches the existing database-authorized identity. Cosmos public access stays disabled.

The template enables HTTPS, liveness monitoring and filesystem logs, disables FTP/SCM basic publishing credentials, and persists pagination keys under `/home/data`. Browser sign-in and enrollment default to disabled. It makes no Entra changes. Every supported tier incurs ongoing charges and requires available regional subscription quota; run an ARM what-if before creating resources. Infrastructure deployment does not publish application code: separately publish `Factory.Plant.Service` for `linux-x64`, zip the publish-directory contents at the archive root, and deploy that ZIP using Entra-authenticated `az webapp deploy`. The configured startup command runs `Factory.Plant.Service.dll`.

The hosting template alone does not complete website or enrollment setup. The selected Entra registration must separately expose the required delegated scope and app roles before authorized clients can use those flows. `/health/live` does not verify Cosmos or caller authentication.

### Production browser sign-in

[`website.production.bicep`](website.production.bicep) adds website support to an existing deployment: private, key-auth-disabled Blob storage with a `data-protection` container; a private RBAC Key Vault with a software RSA cookie-protection key; two private endpoints; private DNS zones and links to the application VNet. Vault public network access stays disabled; its trusted Azure-services bypass allows ARM to provision the key and secret. The existing data identity receives Blob Data Contributor at the container and Crypto User at the key. It does not redeploy Cosmos, the plan, the app or the VNet.

After explicit approval, append the production HTTPS `/signin-oidc` and `/signout-callback-oidc` URLs to the selected Entra registration without removing existing callbacks. Create an additional, bounded-lifetime confidential-client credential without resetting existing credentials. [`website-credential.production.bicep`](website-credential.production.bicep) stores it in Key Vault and grants the data identity Secrets User at that secret only. Its `clientSecret` parameter is secure; submit the deployment through an in-memory ARM request. Never place credential values in parameter files, shell arguments, deployment outputs or source control. Set `expiresOn` to the credential's Unix expiration timestamp and rotate before expiry.

Only after these prerequisites are ready, deploy `service.production.bicep` with `webEnabled=true`, `webPublicOrigin`, `dataProtectionBlobUri`, `dataProtectionKeyId` (versionless) and `webClientSecretUri` (versionless), using the support deployment outputs. Preserve these non-secret parameters for later hosting deployments; omitting them disables browser sign-in. The app uses its assigned data identity for the Key Vault reference. Linux App Service private Key Vault references require VNet route-all; the template enables it with browser sign-in. Ensure that subnet routing permits public Entra egress. API-only hosting routes private destinations through the VNet without route-all.

Use the application's OIDC authentication, not App Service Easy Auth. Users need `Factory.User`; plant creation also requires `Factory.PlantCreator`. Verify the live HTTPS authorization-code/PKCE challenge, protected-cookie key access and bearer-only `/v1` boundary. A successful challenge is not a completed human login or a Cosmos data-plane check. Workbench enrollment is a separate configuration.

### Production workbench enrollment

[`enrollment-identity.production.bicep`](enrollment-identity.production.bicep) creates only a dedicated user-assigned managed identity. It grants no Azure data roles or Graph permissions and does not change the app. Supply that identity's name as `provisioningIdentityName` to `service.production.bicep` to retain both the data identity and provisioner on later hosting deployments. The website's Key Vault reference continues to select the data identity.

Complete the [privileged enrollment bootstrap](../bootstrap/tenant/README.md#enable-workbench-enrollment) only after explicit approval of the Graph grants. Set `installerClientId` and `enrollmentEnabled=true` in the hosting parameters when the public installer and managed provisioner are configured. Production provisioning is managed-identity-only; never reuse the data identity. Preserve the existing browser-authentication parameters. The [live installation working document](../../../docs/workbench-installation-test.md) tracks package handoff and machine acceptance separately from service deployment.

### Private cross-region Cosmos access

[`backend-peering.bicep`](backend-peering.bicep) connects existing, non-overlapping application and Cosmos VNets in the same resource group. Supply `appVnetName` and `cosmosVnetName`. It creates both peering directions with virtual-network access enabled and forwarding/gateway transit disabled. It does not create a web app or database, grant data access, or enable public Cosmos access. Cross-region peering incurs traffic charges.

The application must integrate with its local delegated subnet; both VNets need links to the Cosmos private DNS zone. Configure the future app with the existing Cosmos endpoint, selected database/container, and an assigned managed identity already authorized for that database. Peering does not grant Cosmos data permissions. Verify DNS resolution and an authenticated database operation from the application after it is deployed; connected peerings alone do not prove application connectivity.

## Shared Cosmos settings

| Setting | Definition |
|---|---|
| API / data | Cosmos DB for NoSQL; database `plant`, container `records`, partition key `/scopeId`. |
| Consistency / backup | Strong; continuous backup with 30-day point-in-time retention. |
| Capacity | Container autoscale, explicit `MaxThroughput` ceiling; bootstrap default 1,000 RU/s. |
| Indexing | Consistent, selected record/query paths; system `id` and `_ts` indexes are implicit. Never explicitly include `/id/?`. |
| Authentication | Entra tokens only, account-key/local authentication disabled, minimum TLS 1.2. |
| Permission | Cosmos DB Built-in Data Contributor scoped to `/dbs/plant`, assigned to the ASP.NET service identity. |
| Availability | One region, no zone redundancy configured. Backup retention is not a tested recovery procedure. |

Browser sign-in authenticates users **to ASP.NET**. The ASP.NET process separately obtains an Entra token as its service identity **to Cosmos**. Neither network reachability nor browser user roles grant Cosmos data access. Databases/containers are provisioned by Bicep, never created at application startup.

## Environment differences

| | Development | Production |
|---|---|---|
| Service identity | Existing development application service principal from tier 1. | Newly created user-assigned managed identity. |
| Service credential | Client secret in local .NET user-secrets. | Managed-identity token; no Cosmos/app credential secret. |
| Public Cosmos access | Enabled; all sources by default, optional explicit IPv4 allowlist. | Disabled. |
| Network resources | None. | VNet, delegated `app-service` subnet, endpoint subnet, Cosmos private endpoint, `privatelink.documents.azure.com` zone and VNet link. |
| Tenant prerequisite | Development app registration, callbacks and user assignments. | No app registration needed for the backend-only tier. |

The shared module defaults to **public access Disabled**. Only the nonproduction wrapper enables it. An empty IP list alone never enables public access.

Backend-only production deployments reserve the application subnet for later service hosting. A machine outside the VNet has no production data path. Development defaults deliberately favor local reachability while retaining mandatory Entra service authentication.

## Cost, permissions and operations

Cosmos provisioned autoscale capacity, data/backup storage, production private endpoint and networking can incur charges even without a running website. The throughput ceiling is not a spending cap or price estimate. Approve region/capacity/retention/network ranges before deployment; subscription offers and quotas vary. Scripts prompt before writes and support `-WhatIf`, which is a PowerShell preview, not ARM quota/cost validation.

Operators need resource/provider/group creation rights as applicable and permission to write Cosmos SQL role assignments. Entra application and user-role management uses separate directory privileges. Scripts do not acquire broader permissions, create billing subscriptions, or retry failed login interactively.

ARM success and output metadata establish control-plane provisioning only. Verify real authenticated data operations from the intended network/service identity. For production hosting, private DNS resolution, identity attachment, application deployment, diagnostics/alerts and recovery exercises remain separate work.

## Alternative full-stack scaffold: not used by bootstrap

`main.bicep` additionally defines Linux App Service, private Blob containers (including a Data Protection key ring), private RBAC Key Vault, monitoring, managed identities and private network integration. It requires separately provisioned browser credentials/cookie-protection key before browser enablement; it creates neither object. Application monitoring integration is not complete.

**Do not run `main.bicep` unchanged after a supported standalone backend deployment.** Its naming/layout currently creates a different Cosmos account, identity and network. Reusing the backend outputs must be implemented when website hosting is added. Neither current bootstrap script invokes it.

The `.example.bicepparam` files are illustrative inputs, not deployment approval. The scripts assemble non-secret parameters and use the same environment templates.

## Offline checks

These compile templates without Azure sign-in or deployment:

```powershell
az bicep build --file .\src\Factory\deploy\cosmos.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\cosmos.nonproduction.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\service.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\website.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\website-credential.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\enrollment-identity.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\backend-peering.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\main.bicep --stdout | Out-Null
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\Bootstrap.Tests.ps1
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\EnrollmentBootstrap.Tests.ps1
```

Local checks cannot establish quota, deployment authorization, data-plane connectivity or a running website.
