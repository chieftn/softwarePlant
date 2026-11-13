# RFC 003: Factory Solution and Projects

Status: Draft proposal. This defines the next iteration's project structure; it does not create implementation projects.

Companions: [RFC 001: Workbench Workloads and Installation](001-workbench-workloads-and-installation.md) and [RFC 002: Plant Service](002-plant-service.md).

## Scope and Naming

Factory names the overall application and project family. A plant supplies execution infrastructure. A workbench is one execution environment within that plant; it is not the name of the server, shared protocol, or solution.

Use `Factory` for the solution and root namespace. Use `Factory.Plant` and `Factory.Workbench` where responsibilities are specific to those components. Domain terms do not imply one project each.

## Solution Layout

Specify the implementation under `src\Factory`:

```text
src\Factory\
    Factory.slnx
    Factory.Contracts\
    Factory.Plant.Service\
    Factory.Workbench.Host\
    Factory.Workbench.Worker\
    Factory.Tests\
    setup\
    deploy\
    publish.ps1
```

| Project | Responsibility | Principal dependencies |
|---|---|---|
| `Factory.Contracts` | Versioned API payloads, assignment snapshots, configuration, results, events, and shared structural validation. | .NET serialization; no hosting, database, or agent SDK. |
| `Factory.Plant.Service` | HTML/Datastar web application, JSON API, plant ownership/scoped access, durable background Entra provisioning, line bindings, queues, claims, persistence, artifacts, and server telemetry. | Contracts, ASP.NET Core, Entra OpenID Connect/token validation, Microsoft Graph, Cosmos DB, Azure Storage, Azure Monitor, and self-hosted Datastar. |
| `Factory.Workbench.Host` | Windows configuration and certificate access, outbound service connection, local policy, durable journals, WSL process management, and result delivery. | Contracts, Azure Identity/MSAL as required, Windows and process APIs. |
| `Factory.Workbench.Worker` | Linux checkout, dependency preparation, MAF/Copilot execution, independent verification, commit, permitted publication, and execution evidence. | Contracts, MAF/Copilot SDK, Git, and the GitHub adapter. |
| `Factory.Tests` | Contract, scoped authorization, browser/API authentication, provisioning recovery, queue-ownership, policy, and execution tests; service/host integration and synthetic fleet scenarios. | The production projects and test tooling. Platform-specific checks require the corresponding Windows/WSL or deployed test environment. |

Target .NET 10. Host and worker are separate executables because their operating environments and dependencies differ. The worker executable manages an assignment's execution; the domain worker is the agent it hosts.

## Dependency Boundaries

The service, host, and worker reference `Factory.Contracts`, not each other's implementations.

```text
Factory.Plant.Service ----\
Factory.Workbench.Host ---+--> Factory.Contracts
Factory.Workbench.Worker -/
```

The host calls the service over HTTPS and launches the Linux worker through WSL using serialized contracts. The service does not reference the worker executable, load MAF to process queue requests, or access workbench credentials.

The web application and JSON endpoints share application operations and authorization. They need no separate deployment or frontend application framework. Background provisioning is a hosted worker in the service project with durable reservations, not a request handler holding the user's session open.

Contracts define data and structural validity. The service owns admission and ownership decisions. The host owns local permission limits. Worker execution cannot bypass either by changing a payload.

Keep GitHub operations inside the worker's source-control/publication component. Keep server telemetry exporters inside the service, and execution-event collection inside the host/worker. Use explicit configuration and narrow component boundaries; separate integration projects are unnecessary until concrete reuse requires them.

## Production-Line Coordination

A production-line coordinator is a client of the plant API. It may use MAF for decomposition, coordination, and interpretation of results. It is not part of the service's claim transaction or the workbench host's connection loop.

Do not add a generic orchestration project before specifying the first coordination workload. The API must also accept authorized manual submissions, so plant and workbench behavior can be exercised independently of an agent coordinator.

## Setup and Deployment

`setup` contains guided workbench installation, configuration, inspection, and startup tooling specified by RFC 001.

`deploy` contains Azure infrastructure definitions, service deployment, and the one-time privileged Entra bootstrap tooling specified by RFC 002. Normal device provisioning runs in the service using its administrator-approved provisioning identity. Human directory credentials must not become service or workbench runtime dependencies; the installer and daemon receive no Graph permissions.

`publish.ps1` produces two independently deployable outputs: a Linux plant-service package and a Windows/WSL workbench package. The workbench package contains matching host and worker builds. Each output carries a release manifest, source revision, file digests, and supported protocol/schema versions.

Deploying the service does not install a workbench. Installing a workbench does not provision Azure or start production work. Compatibility checks govern communication between independently deployed versions.

## Completion Criteria

- The solution and shared contracts use Factory naming; Workbench names only the execution components.
- The service builds and operates without a MAF/Copilot runtime dependency.
- The host and worker require no database SDK or direct data-store access.
- The plant view and agent coordinators use the same authorized service operations as other clients.
- The HTML/Datastar application uses Entra sign-in/cookies; the daemon uses its own certificate-backed bearer identity, with shared plant-scoped authorization.
- Enrollment survives user logout and server restart without an operator provisioning script or duplicate directory identities.
- Service and workbench packages can be published separately, with explicit compatibility and matching host/worker release identification.
