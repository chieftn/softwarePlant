# Factory

ASP.NET Core/.NET 10 REST service and Razor website for [RFC 003](../../rfcs/003-factory-solution-and-projects.md). The existing `src\Workbench` implementation and deployment are unchanged.

## Bootstrap

Run commands from the repository root in **PowerShell 7**. Provisioning also requires Azure CLI/Bicep and an explicitly selected tenant/subscription. Scripts prompt before cloud changes; `-WhatIf` checks inputs and describes actions without provisioning.

| Tier | Guide | Responsibility |
|---|---|---|
| 1. Tenant | [Entra bootstrap](bootstrap/tenant/README.md) | Create or adopt the application/service principal, configure sign-in and API roles/scopes, and assign selected users. |
| 2. Production | [Production backend](bootstrap/production/README.md) | Create the resource group, private Cosmos DB, managed identity and network. **Backend only; no website deployment.** |
| 3. Development | [Azure harness and local configuration](bootstrap/development/README.md) | Provision separate development Cosmos, then configure standard .NET user-secrets and local HTTPS. |

Development needs tier 1 followed by tier 3. The current production backend uses managed identity and can be provisioned independently; a production website registration is only needed when hosting is added.

[Infrastructure inventory](deploy/README.md) documents the deployed resource definitions, identity/network boundaries, cost settings and what is not provisioned. Update it with infrastructure changes.

Bootstrap handoff files contain identifiers/endpoints, not credentials, and default to ignored `src\Factory\artifacts\bootstrap`. Secret values are entered only at the local hidden prompt, never in commands or committed files. No Azure CLI login is required by the configured local application.

## Current scope

| Location | Implemented |
|---|---|
| `Factory.Contracts` | Plant and workbench registration/presence contracts and structural validation. |
| `Factory.Plant.Service` | Entra bearer API, separate browser sign-in, scoped authorization, Cosmos persistence and Razor plant/workbench pages. |
| `Factory.Tests` | Isolated HTTP, authorization, transactions, OIDC/cookies/forms, serialization and OpenAPI checks. |
| `api` | [Contract for the eight REST operations](api/README.md). |
| `bootstrap` / `deploy` | Tiered setup scripts and shared/environment-specific Bicep. |

The website lists, creates and displays plants and shows workbench inventory. Plant creation atomically records ownership, idempotency and audit data. REST authentication remains bearer-only; browser cookies never authenticate `/v1`.

Enrollment, machine admission, assignment execution, group resolution and workbench release packaging are not implemented. Inventory may be empty; there is no runtime seed, demonstration identity or local persistence substitute. Keep the existing Workbench projects unchanged.

`GET /health/live` proves process liveness only, not Azure connectivity or successful authentication.

## Build and checks

```powershell
dotnet test .\src\Factory\Factory.slnx
pwsh -NoProfile -File .\src\Factory\bootstrap\tests\Bootstrap.Tests.ps1
```

The bootstrap checks mock Azure CLI, dotnet and secret prompts; they do not provision resources or read real user-secrets.

For an unconfigured public landing-page preview only:

```powershell
dotnet run --project .\src\Factory\Factory.Plant.Service --no-launch-profile -- --urls http://localhost:5080
```

That preview does not enable sign-in or data access. Use the development guide for the working local site.
