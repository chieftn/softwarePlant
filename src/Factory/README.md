# Factory

Initial service and infrastructure foundation for [RFC 003](../../rfcs/003-factory-solution-and-projects.md). The working `src\Workbench` implementation and its deployment are unchanged.

## Current Scope

| Location | Implemented |
|---|---|
| `Factory.Contracts` | Dependency-free shared project, with no speculative API payloads yet. |
| `Factory.Plant.Service` | ASP.NET Core/.NET 10 executable; public static HTML landing page and `GET /health/live`. |
| `Factory.Tests` | In-process HTTP checks for the landing page, liveness, and explicit absence of unimplemented operations. |
| `api` | [Plant and workbench REST contract](api/README.md) for plant operations, workbench inventory, registration and presence; not runtime handlers. |
| `deploy` | Undeployed Bicep definitions for a separate development environment. |

Liveness proves only that the HTTP process responds. It does not check Azure, authentication, enrollment, or workbench connectivity. No plant state or operational details are exposed. Unimplemented routes return `404` Problem Details, not fake successful responses.

There is no sign-in, plant creation, scoped authorization, datastore client, provisioning worker, or daemon endpoint yet. The page has no interactive state, so it does not select a server template renderer or bundle Datastar. Select the renderer and pin a self-hosted Datastar version when adding authenticated interactions. Do not expose plant operations before implementing Entra authentication and the shared authorization rules.

The host and worker projects, guided setup, and combined release packaging remain future work. Do not rename or migrate the existing Workbench projects as part of this foundation.

## Local Development

Requires the .NET 10 SDK. No Azure credentials or resources are required:

```powershell
dotnet test .\src\Factory\Factory.slnx
dotnet run --project .\src\Factory\Factory.Plant.Service -- --urls http://localhost:5080
```

Open `http://localhost:5080`. Local HTTP is for this public foundation only; App Service enforces HTTPS.

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
- Private Blob containers for snapshots, evidence, archives, and the future Data Protection key ring; versioning and 30-day blob/container soft deletion.
- An RBAC-enabled Key Vault with purge protection and 90-day soft deletion. No credentials or keys are created.
- Private endpoints and linked private DNS for Cosmos, Blob, and Key Vault. Public data access and Cosmos/Storage key authentication are disabled.
- Separate data-access and provisioning managed identities. The data identity receives database-scoped Cosmos access, container-scoped Blob access, and Application Insights publishing access. The provisioner receives **no Graph permissions**.
- Application Insights with local authentication disabled and a Log Analytics workspace retaining diagnostics for 30 days.

The application does not consume these resources yet. No Azure SDK credentials or exporter are configured. Future code must explicitly select the appropriate identity. Key Vault secret/key permissions will be added at the specific object scope when web sign-in and cookie protection are implemented. The two attached identities share the App Service process boundary; they do not isolate provisioning.

The example parameters are illustrative, not deployment approval. Before deployment, confirm the tenant, subscription, separate resource group, naming, region, non-overlapping network ranges, SKU/RU budget, retention, and authority to assign Azure data roles. Choose a real region in a separate parameter file; do not deploy the example unchanged. The deployment identity needs resource creation and role-assignment authority, not Graph permission.

Local compilation does not need Azure sign-in:

```powershell
az bicep build --file .\src\Factory\deploy\main.bicep --stdout | Out-Null
az bicep build-params --file .\src\Factory\deploy\main.example.bicepparam --stdout | Out-Null
```

No deployment command is included. Local compilation does not verify Azure availability, quota, cost, data-plane access, private DNS resolution, or actual App Service startup. Deployment/what-if and runtime checks require separate authorization.

This is not a production deployment: autoscale, multiple instances, staging isolation, diagnostics/alerts, archive retention, restore exercises, Entra registration/bootstrap, shared cookie keys, and deployment automation remain to be implemented. Never use this scaffold to change the existing live workbench's identity, endpoint, or journals.

## Next Complete Slice

Add Entra web sign-in, durable plant creation with an atomic owner binding, shared application authorization, and an HTML plant view. Demonstrate denial across separate test plants before adding enrollment or Graph writes. Resolve the renderer and local/deployed identity test setup before that work begins.
