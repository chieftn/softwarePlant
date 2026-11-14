# Tier 2: production backend

This provisions **Cosmos DB and its managed identity/private network only**. It does not publish the ASP.NET service, create an App Service, or make a production website available.

Use an existing, approved Azure subscription in the selected tenant; creating a billing subscription is an administrator prerequisite, not part of this script. A new subscription can be targeted in exactly the same way. The operator needs rights to register resource providers, create the resource group/resources, and assign Cosmos SQL data roles. Azure and directory permissions are separate.

## Deploy

Run from the repository root in PowerShell 7, with Azure CLI and Bicep available:

```powershell
$tenant = '<production-tenant-id>'
$subscription = '<production-subscription-id>'
az login --tenant $tenant

.\src\Factory\bootstrap\production\Deploy-Production.ps1 `
    -TenantId $tenant -SubscriptionId $subscription `
    -ResourceGroup 'factory-prod' -Location 'westus2' `
    -NamePrefix 'factory' -MaxThroughput 1000
```

Choose the region, resource group and RU/s ceiling intentionally; the example is not sizing advice. The ceiling must be a multiple of 1,000. The confirmation includes the tenant/subscription/group and billable throughput. Add `-WhatIf` for a **PowerShell preview**, not an ARM what-if or quota/price validation. It does not create even the resource group.

The script checks tenant/subscription context, registers missing required providers, creates the group if absent, and applies [`cosmos.production.bicep`](../../deploy/cosmos.production.bicep). A failed CLI command stops immediately; it never starts an interactive login on your behalf.

## Infrastructure and identity

| Resource | Purpose |
|---|---|
| Cosmos account, `plant` database, `records` container | Strong consistency, `/scopeId` partitioning, 30-day continuous backup and approved autoscale ceiling. Account keys are disabled. |
| User-assigned managed identity | Later attached to the ASP.NET service; Cosmos DB Built-in Data Contributor scoped to `/dbs/plant`. No client secret. |
| VNet and `app-service` subnet | Reserved VNet integration subnet for the later service. |
| `private-endpoints` subnet and Cosmos private endpoint | Production data access; public Cosmos access is disabled. |
| Private DNS zone and VNet link | Resolve Cosmos privately from the application network. |

See the maintained [infrastructure inventory](../../deploy/README.md) for shared settings, costs and boundaries.

Default address ranges are `10.42.0.0/24`, application subnet `10.42.0.0/26`, endpoint subnet `10.42.0.64/27`. For a new environment that needs different non-overlapping ranges, supply `-VnetAddressPrefix`, `-AppSubnetPrefix` and `-EndpointSubnetPrefix` together. Keep those values on reruns; changing a live subnet's address space is not a migration mechanism.

## Outputs and reuse

The default `src\Factory\artifacts\bootstrap\production.json` contains the target identifiers, Cosmos endpoint/database/container, managed-identity resource/client/principal IDs and application subnet ID. It contains no credentials.

The same subscription, group, name prefix and region reuse the same resource names as the existing standalone production template. For an existing `factory-prod` deployment, keep its original arguments; do not change the prefix to "adopt" it. Existing groups must be tagged `application=Factory` and `environment=production`; the script refuses unrelated/untagged groups. Review a group's contents before deliberately adding those tags.

Rerun the same command after a failed Azure deployment; ARM reconciles existing resources rather than requiring deletion. Use a separate `-OutputPath` for a second production environment. Changing the RU/s ceiling does not guarantee Cosmos can lower throughput below its service-enforced historical/data-size minimum.

**Completion means an ARM deployment succeeded, not a running service or a data-plane test.** A laptop outside the VNet cannot use this private endpoint. Later hosting must reuse the output managed identity/subnet and provide its own Entra browser registration, secret storage and shared cookie key ring. Do not deploy the alternative full-stack `main.bicep` unchanged: it currently creates different Cosmos/network resources.
