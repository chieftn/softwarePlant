# Factory

Initial service and infrastructure foundation for [RFC 003](../../rfcs/003-factory-solution-and-projects.md). The working `src\Workbench` implementation and its deployment are unchanged.

## Current Scope

| Location | Implemented |
|---|---|
| `Factory.Contracts` | Plant, workbench registration/presence and response payloads with structural validation; no hosting or SDK dependencies. |
| `Factory.Plant.Service` | ASP.NET Core/.NET 10 REST operations and Razor MVC views, separate Entra bearer/browser authentication, scoped authorization, Cosmos persistence, public landing page and `GET /health/live`. |
| `Factory.Tests` | Isolated HTTP/authorization/transactional checks, browser OIDC/cookie/form flows, Cosmos record serialization, and OpenAPI validation. |
| `api` | [Plant and workbench REST contract](api/README.md) for the eight implemented operations. |
| `deploy` | Undeployed Bicep definitions for a separate development environment. |

Liveness proves only that the HTTP process responds. It does not check Azure, authentication, enrollment, or workbench connectivity. No plant state or operational details are exposed. Unimplemented routes return `404` Problem Details, not fake successful responses.

The eight REST operations support plant list/get/create/update and workbench inventory, registration and presence. Creation atomically records the plant, owner binding, idempotency outcome and audit event. Updates require ownership and If-Match, and replay checks precede new-write preconditions. Registration/presence require the exact active application identity and never admit machines or change execution ownership. Presence is stored separately from registration and admission; retries do not refresh contact.

The website lists plants, creates plants, and shows plant and workbench details. Razor views and ordinary HTML forms call `PlantOperations` directly, without loopback HTTP or duplicated bookkeeping. There is no React shell, Node runtime, or browser-side domain model. This slice does not need live updates and does not bundle Datastar or another interaction library.

Enrollment/provisioning, workbench admission and assignment execution are not implemented. Workbench pages only show records already admitted through a separate process; there is no runtime seed or demo identity.

The host and worker projects, guided setup, and combined release packaging remain future work. Do not rename or migrate the existing Workbench projects as part of this foundation.

## Local Development

Requires the .NET 10 SDK. No Azure credentials or resources are required:

```powershell
dotnet test .\src\Factory\Factory.slnx
dotnet run --project .\src\Factory\Factory.Plant.Service -- --urls http://localhost:5080
```

Open `http://localhost:5080`. This starts the public Razor landing page, not an authenticated demo. Without API configuration, page/liveness still work and `/v1` returns explicit `503` Problem Details with a startup warning listing missing fields. Browser sign-in defaults to disabled; `/plants` and `/account` return an explicit HTML `503` until it is configured. There is no anonymous identity, credential-chain or in-memory runtime fallback.

### Entra and Cosmos Configuration

[`configuration.example.json`](configuration.example.json) contains non-secret configuration placeholders; it is not loaded automatically. Supply real values through environment variables (`Factory__TenantId`, etc.) or a local `Factory.Plant.Service\appsettings.Development.json` file. Do not commit credentials or tokens. The compatibility arrays are explicit operator-approved registration versions, not a claim that the service implements assignment execution.

| Setting | Meaning |
|---|---|
| `Factory:TenantId`, `Factory:ClientId` | Single Entra tenant and API application client ID. Version-2 access tokens must match issuer, audience, signature and lifetime. |
| `Factory:CosmosEndpoint`, `CosmosDatabase`, `CosmosContainer` | Existing HTTPS NoSQL endpoint and resources (`plant`/`records` by default), with `/scopeId` partitioning and the indexes in Bicep. No databases or containers are created at startup. |
| `Factory:DataCredential` | Explicit `ManagedIdentity`, or `AzureCli` only when `ASPNETCORE_ENVIRONMENT=Development`. No automatic credential fallback or login. |
| `Factory:DataIdentityClientId` | Required user-assigned managed identity client ID for `ManagedIdentity`. It must have database-scoped Cosmos data access. |
| `Factory:SupportedProtocolVersions`, `SupportedAssignmentSchemaVersions` | Nonempty arrays governing daemon registration compatibility; at least one advertised assignment version must overlap. |
| `Factory:PaginationKeyRingPath` | Optional persistent shared directory for API-only Data Protection keys. Browser mode replaces this with the required Blob/Key Vault key ring. |

For explicitly configured local Azure access, use Development plus `DataCredential: "AzureCli"` and independently establish the intended tenant login and narrowly scoped Cosmos data access. These instructions do not sign in, create registrations, grant permissions or connect to live resources.

Human API tokens need delegated `Plant.Access` and `Factory.User`; creation additionally needs `Factory.PlantCreator`. Plant owners can read/update their plants and read their workbenches. Current `Plant.Reader`, `Plant.Operator` and `Workbench.Registrar` assignments grant scoped reads; `Plant.EnrollmentRequester` grants plant-summary reads only. Resource-scoped grants do not confer plant-wide access. Group resolution is not implemented.

Workbench writes need an application `Workbench.Connect` token and an existing active enrollment binding containing tenant ID, service-principal object ID and client ID. Enrollment is not implemented, so this slice does not create admission records or offer a runtime seeding bypass. The isolated tests seed admission/access records only inside the test store.

Requests reject unknown/duplicate fields, unsupported versions and invalid bounds. Bodies are limited to 128 KiB. API responses use JSON/Problem Details and never redirect to browser login. A per-instance limiter allows 120 requests/minute per authenticated principal (or anonymous remote address); Cosmos throttling and bounded transaction contention return retry guidance.

Pagination tokens are protected, expire after one hour, and are bound to caller, route/plant and page size. Current authority is rechecked on each request. API-only App Service uses its persisted `/home` key-ring directory; browser mode shares a Blob/Key Vault-protected key ring for pagination, cookies, OIDC state and antiforgery.

Enabling browser mode changes the key-ring location. Existing API pagination cursors become invalid unless the old keys are deliberately migrated; plant records and mutation receipts do not depend on these keys.

### Browser Sign-in and Razor Pages

Browser sign-in uses `Microsoft.Identity.Web` with the same single-tenant Entra application client ID as the API. It uses authorization code with PKCE and a secure, HTTP-only `__Host-Factory.Session` cookie. It does not save access/refresh tokens in the cookie or browser storage, call Graph, or use App Service Authentication headers. The JSON API remains explicitly bearer-only and never redirects to login.

| Route | Behavior |
|---|---|
| `/` | Public landing page; no inventory is exposed before sign-in. |
| `/plants` | Paginated list of plants the current user can read. |
| `/plants/create` | Creation form; requires `Factory.PlantCreator` in addition to `Factory.User`. |
| `POST /plants` | Antiforgery-protected creation with a stable per-form operation key and owner bound to the signed-in tenant/object ID. Failed submissions retain their input/key for retry. |
| `/plants/{plantId}` | Plant details and link to workbench inventory. |
| `/plants/{plantId}/workbenches` | Paginated inventory filtered by current scoped access. |
| `/plants/{plantId}/workbenches/{workbenchId}` | Admission, registration and last reported presence shown separately. Refresh reads current state; it is not a live subscription. |
| `/account/signin`, `POST /account/signout` | Entra sign-in and antiforgery-protected sign-out. |

Enable the website only after the API configuration and these prerequisites are available in a separately authorized test environment:

1. Configure the Entra application as a confidential web client with the exact web redirect URI, such as `https://localhost:7443/signin-oidc`, and signed-out callback `https://localhost:7443/signout-callback-oidc`. Disable implicit token grants and App Service Authentication. Assign `Factory.User` and, where appropriate, `Factory.PlantCreator` to test users.
2. Provide an existing private Blob container and versionless Key Vault RSA key. The explicitly selected `Factory:DataCredential` needs Blob access and key-scoped get/wrap/unwrap permission. Local access must reach the private endpoints through an approved network; do not make resources public merely to run the site.
3. Configure the settings below. Keep the confidential-client credential out of source control and chat. Supply it through a process environment variable for local development; deployed App Service uses a Key Vault reference to the existing secret.
4. Use a trusted local HTTPS development certificate. If one is not already established, set it up separately using the supported .NET development-certificate tooling.

| Setting | Meaning |
|---|---|
| `Factory:Web:Enabled` | Explicit opt-in (`true`); read at host startup. Enabling with missing/invalid required configuration fails startup rather than falling back to anonymous/test authentication. |
| `Factory:Web:PublicOrigin` | Canonical HTTPS origin, e.g. `https://localhost:7443`, with no path/query. OIDC callback URLs use this origin, not caller-supplied Host/forwarding headers. |
| `Factory:Web:ClientSecret` | Server-side confidential-client credential. The example file intentionally omits it. An unresolved Key Vault reference is rejected. |
| `Factory:Web:DataProtectionBlobUri` | HTTPS blob URI, e.g. `https://<account>.blob.core.windows.net/data-protection/keys.xml`; no SAS. The private container must already exist. |
| `Factory:Web:DataProtectionKeyId` | Versionless URI `https://<vault>.vault.azure.net/keys/<key>`. Retain old key versions so existing protected data remains readable after rotation. |

With the required values supplied and local HTTPS established:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\Factory\Factory.Plant.Service -- --urls https://localhost:7443
```

Open `https://localhost:7443/plants`. These instructions do not create registrations/resources, sign into Azure, or grant permissions.

Sessions expire after one hour without sliding renewal; a new Entra sign-in refreshes service-entry role claims. Resource grants are read on every operation, so scoped revocation does not wait for cookie expiry. Entra service-entry role changes can take effect only at a new sign-in or session expiry; there is no per-request directory query. Use separate key-ring locations for staging and production and share the application/key-ring configuration only among instances of the same environment.

Local tests use generated RSA signing keys, static issuer metadata, an isolated token endpoint and a transactional store registered only by test code. They exercise real JWT and OIDC middleware, PKCE, cookies, antiforgery, Razor encoding, form retries and scoped reads without contacting Azure. Live Entra sign-in, Cosmos behavior/RU, Blob/Key Vault permissions and persistence, private networking, deployment and recovery still require a separately authorized integration environment.

Publish the service independently as a self-contained Linux x64 build:

```powershell
dotnet publish .\src\Factory\Factory.Plant.Service\Factory.Plant.Service.csproj `
    --configuration Release --runtime linux-x64 --self-contained true `
    --output .\src\Factory\artifacts\plant-service
```

This produces binaries, not a signed release or deployed service. A deployment archive must contain the publish directory's contents at its root. The Bicep startup command runs `Factory.Plant.Service` on port 8080. Release manifests, digests, and supported protocol/schema versions must be added before workbench distribution; this scaffold does not claim release compatibility.

## Infrastructure

`deploy\main.bicep` targets an explicitly selected resource group in the public Azure cloud. It defines:

- One Linux App Service instance on an explicitly selected Standard/Premium SKU, with Always On, HTTPS, VNet integration, and basic publishing credentials disabled.
- Cosmos DB for NoSQL with strong consistency, 30-day continuous backup, database `plant`, and an autoscale `records` container partitioned by `/scopeId`. Queue-specific composite indexes must be added with the actual queries.
- Private Blob containers for snapshots, evidence, archives, and the Data Protection key ring; versioning and 30-day blob/container soft deletion.
- An RBAC-enabled Key Vault with purge protection and 90-day soft deletion. No credentials or keys are created.
- Private endpoints and linked private DNS for Cosmos, Blob, and Key Vault. Public data access and Cosmos/Storage key authentication are disabled.
- Separate data-access and provisioning managed identities. The data identity receives database-scoped Cosmos access, container-scoped Blob access, and Application Insights publishing access. The provisioner receives **no Graph permissions**.
- Application Insights with local authentication disabled and a Log Analytics workspace retaining diagnostics for 30 days.

The service consumes Cosmos and, in browser mode, Blob/Key Vault using the explicitly selected data credential. Bicep wires the managed identity and endpoint settings, and requires an existing API client ID and approved compatibility versions. Monitor application integration remains future work. The two attached identities share the App Service process boundary; they do not isolate provisioning.

Browser sign-in stays disabled by default. After separate authorized bootstrap has created the confidential-client secret and cookie-protection RSA key in this deployment's vault, set both `webSignInSecretName` and `cookieProtectionKeyName`. The template references these existing objects, adds **Secrets User at the individual secret scope** and **Crypto User at the individual key scope**, and configures the App Service Key Vault reference and shared key ring. It never creates or outputs credential values or grants Graph permissions. Enablement requires both names. Account for Azure role propagation and verify reference resolution before treating a deployment as usable.

The example parameters are illustrative, not deployment approval. Before deployment, confirm the tenant, subscription, separate resource group, naming, region, non-overlapping network ranges, SKU/RU budget, retention, and authority to assign Azure data roles. Choose a real region in a separate parameter file; do not deploy the example unchanged. The deployment identity needs resource creation and role-assignment authority, not Graph permission.

Local compilation does not need Azure sign-in:

```powershell
az bicep build --file .\src\Factory\deploy\main.bicep --stdout | Out-Null
az bicep build-params --file .\src\Factory\deploy\main.example.bicepparam --stdout | Out-Null
```

No deployment command is included. Local compilation does not verify Azure availability, quota, cost, data-plane access, private DNS resolution, or actual App Service startup. Deployment/what-if and runtime checks require separate authorization.

This is not a production deployment: autoscale, multiple instances, staging isolation, diagnostics/alerts, archive retention, restore exercises, Entra registration/bootstrap, live shared-key verification, and deployment automation remain to be implemented. Never use this scaffold to change the existing live workbench's identity, endpoint, or journals.

## Next Complete Slice

Wire and verify Entra, Cosmos and the browser key ring against a separate authorized test environment. Keep enrollment/Graph provisioning and multi-plant physical-machine sharing outside this slice. Select any progressive-enhancement/islands tooling when an actual interaction needs it; the current Razor forms do not require a JavaScript framework.
