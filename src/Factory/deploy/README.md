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

### Managed GitHub repository connections

The website's **Repository connections** page automates GitHub App registration,
private-key storage, installation verification and reusable line connection
references. It infers the vault from the existing `Factory:Web:DataProtectionKeyId`
and uses the existing data identity. No per-connection App Service setting or
manual PEM upload is required.

Before using this feature, review and deploy
[`github-connections.production.bicep`](github-connections.production.bicep) once,
with the existing `vaultName` and `dataIdentityName`. It defines a custom role
containing only Key Vault secret-value get/set and assigns it at the selected
vault. **That permission covers all secrets in that vault**, not just GitHub
secret names. It cannot list, delete or purge secrets, manage keys, or change
RBAC. This is a new permission requiring explicit approval; existing website
secret-read and cookie-key grants alone are not sufficient. The module does not
redeploy the app/settings/network, create a vault or call GitHub.

The service checks actual secret write/read access before offering GitHub app
creation, using the noncredential `factory-github-onboarding-access-check`
secret. Registration bundles are then stored as `github-<connection UUID N>`
secrets. Only identifiers, ownership, repository allowlists and lifecycle state
are persisted in Cosmos. Tokens and private keys are never included in forms,
connection records or line definitions.

After service deployment, sign in, open **Repository connections**, give the
connection a name, optionally select an organization, and choose whether it may
publish. Approve app creation on GitHub, install it with **Only select repositories**,
then select **Verify / refresh repository access**. The first implementation
supports installations on the private app's owning account. Reuse that connection
in any of your production lines; no GitHub setup is required on their machines.
Connections are scoped to their Factory owner's Entra identity; organization-wide
sharing/delegation is not implemented. Existing configuration-backed references
remain supported.

Read-only setup requests Contents, Issues and Pull requests read. Publication
opt-in requests Contents and Pull requests write; Issues stays read only.
Changing this permission profile later requires a new reviewed setup; repository
selection can be changed on GitHub and refreshed. Disabled connections deny new
Factory grants and mission authorization, but cannot undo already-issued tokens
or remote side effects. GitHub app uninstall/key rotation is a separate operator
action. Interrupted manifest exchanges never replay credential creation; the
detail page can recover a registration already saved in Key Vault. If no key was
saved, reconcile/delete the incomplete GitHub App before starting another setup.
Once the one-use exchange starts, a bounded 45-second completion path attempts to
preserve the key even if the initiating browser disconnects.

Deployment and permission verification do not establish that the signed-in
website flow, service-identity vault writes or GitHub installation work end to end.
Complete that acceptance with the first connection. Model access and client
rollout remain separate.

Local validation passed 95 focused onboarding, ownership, browser, workload,
mission and API-contract cases. The new Bicep module compiled offline, and
changed-C# whitespace verification and `git diff --check` passed.

Production rollout completed November 16, 2026 with explicit approval of the
vault-wide secret get/set grant. Deployment
`940198a7-2abf-41ad-9419-9496f4ee9250` is active on
`https://factory-prod-qm3qqqsg45-web.azurewebsites.net`; the exact custom role and
vault-scoped data-identity assignment were verified. All 24 existing settings,
authentication, identities and hosting/network configuration were preserved.
Anonymous health, browser sign-in/PKCE and bearer-only API checks passed; agent
execution remains disabled. Signed-in connection creation/vault storage/GitHub
installation remain unverified. The ignored release folder
`artifacts\service\github-onboarding-20261116-001` contains package/source hashes,
preflight/postflight snapshots, ARM preview/deployment/permission evidence and
HTTP acceptance. No client rollout or model activation was performed.

### Copilot subscription authorization

The service provides **Copilot access** (`/copilot-access`). Create its
authorization app, then select **Authorize Copilot subscription** and choose the
GitHub account with the subscription. This is a separate private GitHub App with
no repository permissions; it does not replace or reinstall repository apps and
does not need repository installation. Both app registration and OAuth user
credentials go directly to the existing website vault. The previously approved
vault get/set role supports this path; no additional Azure setting or grant is
introduced.

One authorization belongs to each Factory user and supplies model access for
production lines that user owns. Machines inherit that authority through their
current plant/line assignment; they do not sign in individually. All usage still
belongs to the authorized GitHub account and shares its subscription limits and
organization policies. Successful OAuth proves account authorization, **not**
Copilot entitlement or availability of a selected model. The real SDK session
checks those, and must fail rather than use another identity.

The flow uses protected, owner-bound, expiring state, PKCE S256 and
`offline_access`. User tokens are refreshed before fewer than 70 minutes remain.
Refresh is conditionally owned in Cosmos so concurrent machines cannot consume
the same rotating refresh token. App and token results use attempt-specific
vault secrets; Cosmos contains only metadata and selected credential IDs.
An interrupted one-use exchange/refresh is never replayed automatically:
**Recover saved authorization** can select a result already saved in the vault.
If no user token was saved, explicitly authorize again. If app creation was
interrupted without a saved registration, reconcile the app on GitHub before
explicitly restarting app creation. Old vault versions/attempt secrets are
retained for operator recovery, not automatically deleted.

Workers request only access tokens through the enrollment's bearer-protected
`mission/model-credential` endpoint and owned pipe. It checks the exact executing
mission, frozen model, current membership, binding, repository/issue authority
and owner's model authorization, both before and after acquisition. Refresh
tokens/client secrets stay in the service. Access tokens stay in worker memory;
no token is included in the startup packet, journals or command arguments.
Ordinary plant HTTP requests retain a 30-second deadline; model-credential
requests allow 60 seconds for bounded refresh, with a 65-second pipe deadline.

The SDK uses its session-scoped GitHub token provider with
`UseLoggedInUser=false`, including renewal callbacks; the old
`FACTORY_WORKBENCH_COPILOT_TOKEN` input is no longer used. Host and Linux worker
must be upgraded together because their internal startup contract changed.
The pinned SDK marks the callback experimental (`GHCP001`); the opt-in is
restricted to the adapter and its tests.

**Disable Copilot access** denies fresh credentials and ongoing mission
authorization checks. It does not revoke already-issued GitHub tokens; revoke
the authorized app on GitHub when immediate external revocation is needed.
Tokens are user/app credentials, not GitHub-enforced per-mission/per-model grants.

`IMissionModelAccess` and the provider-tagged credential form the extension point
for future Azure/Entra authentication. Only `github-copilot` is implemented now;
no Azure model resource, identity, quota or billing configuration is added.
After explicit approval, service deployment
`98c21c77-d7e7-4d2f-b47c-022ba4846079` completed November 16, 2026 and supersedes
the repository-onboarding service deployment above. Existing settings (24),
authentication, identities and hosting/network fingerprints were unchanged;
the existing vault grant was reused without new permissions. Anonymous health,
Copilot navigation, browser sign-in/PKCE and bearer-only model-credential checks
passed. Release evidence is under `artifacts\service\copilot-auth-20261116-001`,
including source/package hashes, preflight/postflight and HTTP acceptance.

The pending-setup continuation repair is deployed as
`913b95d4-63a9-4f51-a650-8ba25d4ea8f6`; evidence is under
`artifacts\service\copilot-auth-resume-20261116-001`. Reopening a pending setup
now preserves its operation ID and offers **Continue setup**, without requiring
restart confirmation. Explicit restart is separate and remains required for
expired attempts. Eight focused browser/auth cases and anonymous live checks
passed, with production configuration unchanged.

The host/Linux worker changes are implemented but not installed. Signed-in
Copilot setup and a bounded real SDK entitlement/renewal acceptance run remain
unperformed. No execution enablement, client installation, GitHub App creation
on the user's behalf or model call was performed for this increment.

Sources: [SDK GitHub OAuth](https://docs.github.com/en/copilot/how-tos/copilot-sdk/setup/github-oauth),
[SDK authentication and rotating session tokens](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate),
[GitHub App user tokens/PKCE/offline access](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app).

### Continuous issue queue and merged completion

The authorized service rollout
`59b0d1c2-7788-4018-8b37-c6455bb8f4fd` reached RuntimeSuccessful on November 16,
2026. It adds `mission/next`, eligible-issue discovery and durable selection,
plus independent GitHub verification of explicit `pull_request_merged`
completion. Approval alone never permits another claim; the prior worker's
termination must also be confirmed. Existing task/source adapter boundaries and
disabled-by-default execution remain in place.

All 24 App Service settings, browser/API authentication, both managed identities,
Key Vault selection/grants and hosting/network fingerprints matched preflight.
No infrastructure template, permission, credential or execution setting was
changed. Health/homepage, protected browser code/PKCE challenges and the new
queue route's anonymous 401/Bearer response passed. These checks do not claim a
live model workflow.

The self-contained Linux service archive's 387 entries matched the publish tree;
69 focused contract/service/worker/host cases passed. Evidence is under
`artifacts\service\continuous-queue-20261116-002`, with the previous
`copilot-auth-resume-20261116-001` archive retained for code rollback.
Matching MSI `0.1.8` is built but not installed by the assistant. The product
workload still needs completion/verification declarations and eligible issues
before separate execution activation.

### Managed workbench activation - November 17 deployment

Local changes add optional execution settings to the existing production-line
definition/form and guided Start/Stop line actions to Workbench Setup.
The approved code-only deployment `cfca26c6-d010-41a7-8d52-9f9e5ff4f279`
is RuntimeSuccessful. The self-contained Linux archive has 387 verified entries,
includes .NET/ASP.NET Core `10.0.15`, and is recorded under
`artifacts\service\managed-execution-20261117-001`. Its SHA-256 is
`A1E6A8744A61A6B508664D84BC441BA019AC7B74D40FCFF76FFD72B95E662BAA`.

All 24 App Service settings, browser/API authentication, both managed identities,
networking and existing vault grants matched preflight. Anonymous health/browser
OIDC/PKCE and protected API/queue challenge checks passed. No Azure permission,
credential migration, line configuration, execution setting or model call was
changed/performed. The prior `continuous-queue-20261116-002` archive is retained
as code rollback evidence; it does not understand new managed execution fields,
so any later rollback after those fields are used needs compatibility review.

Matching unsigned MSI `0.1.9` / `workbench-0.1.9-managed-001` is built and
statically verified, not installed by the assistant. MSI `0.1.8` lacks these
actions. In-place upgrade and preparation on the dedicated test machine precede
separately approved live activation. Retooling automation, central operational
Start/Stop and blanket enrollment delegation remain deferred.

An unset `Factory:Development:Enabled` now permits only explicitly enabled
line-managed execution, with the line's model/review policy and independent
local consent. Existing lines without an execution definition remain disabled
unless legacy service policy explicitly enables them. Explicit `false` remains
a global shutdown override; do not remove or change an existing override as a
side effect of deployment. Review it only as part of separately approved
activation. Legacy service reviewer/deadline settings continue to serve legacy
lines; managed lines receive those values through the normal owner-scoped form.
No new secret, per-device token or user impersonation is needed.

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
az bicep build --file .\src\Factory\deploy\github-connections.production.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\backend-peering.bicep --stdout | Out-Null
az bicep build --file .\src\Factory\deploy\main.bicep --stdout | Out-Null
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\Bootstrap.Tests.ps1
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\EnrollmentBootstrap.Tests.ps1
```

Local checks cannot establish quota, deployment authorization, data-plane connectivity or a running website.
