# Factory infrastructure inventory

Keep this inventory current whenever templates or bootstrap behavior change. It describes **what the templates provision**, not a claim that every target subscription has been deployed or runtime-verified.

## Supported bootstrap entry points

| Environment | Script | Template |
|---|---|---|
| Azure-backed local development | [`Deploy-Development.ps1`](../bootstrap/development/Deploy-Development.ps1) | `cosmos.nonproduction.bicep` |
| Production backend only | [`Deploy-Production.ps1`](../bootstrap/production/Deploy-Production.ps1) | `cosmos.production.bicep` |

Both use `cosmos-resources.bicep`. Subscriptions must already exist. Scripts require explicit subscription/resource-group/region targets; they create missing groups, register required providers and reject unrelated existing groups. They do not change the existing Workbench deployment.

Resource names are deterministic from the subscription/resource-group ID, prefix and environment. Preserve those inputs to reuse existing standalone-template resources. Different environments must use separate groups and identities. Handoff files default to ignored `src\Factory\artifacts\bootstrap`; they contain identifiers/endpoints only.

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

Production reserves the application subnet for later service hosting; no service is attached yet. A machine outside the VNet has no production data path. Development defaults deliberately favor local reachability while retaining mandatory Entra service authentication.

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
az bicep build --file .\src\Factory\deploy\main.bicep --stdout | Out-Null
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\Bootstrap.Tests.ps1
```

Local checks cannot establish quota, deployment authorization, data-plane connectivity or a running website.
