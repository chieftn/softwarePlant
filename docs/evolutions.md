# Evolutions

This document records implementation history, the reasons for proposed changes, and transition requirements. The RFCs describe the proposed behavior without that history.

## November 12, 2026: First Workbench and PR

The first execution path connected a Windows workbench, `boxone`, to a coordinator hosted in Azure Foundry. The Windows host launched a Linux worker under Ubuntu in WSL. The worker used MAF's Copilot integration for coding; ordinary application code handled assignment delivery, Git operations, verification, and publication.

The connection exercise established authenticated registration and advancing server-observed presence. A later dispatched task produced [productionOutput1 PR #1](https://github.com/chieftn/productionOutput1/pull/1). Independent read-back verified the expected branch, commit, base, and added document. This demonstrated one machine and one assignment, not fleet capacity or recovery.

The [installation runbook](installation.md) and [rev1 learnings](rev1-learnings.md) record the earlier connection milestone. They were written before the subsequent PR and should be read in that order.

### Installation

Enrollment required manual application identity creation, certificate creation/export, endpoint-consumer role assignment, caller-identity discovery, coordinator configuration and redeployment, and creation of a version-pinned Foundry session. Startup depended on terminal environment variables and a PFX password.

The supplied package initially did not contain the connection features present in source. Publishing matching host and worker binaries resolved that discrepancy. Source, package, installation, running process, and successful communication proved to be separate states requiring separate evidence.

An initial registration returned HTTP 403. Later probes verified the expected identity and configuration, and a complete startup block succeeded. The original cause was not conclusively isolated.

These experiences led to the guided installation, explicit credential selection, resumable enrollment, and separate readiness checks in [RFC 001](../rfcs/001-workbench-workloads-and-installation.md). That proposal uses a certificate-store credential rather than an exported PFX and removes manual hosted-session setup.

### Shared Plant Record

The coordinator persisted workbench registrations and assignment ownership in one shared document under the key `plant`. Full requests and results were separate immutable items. Conditional ETag updates made ownership changes atomic across coordinator instances.

Idle workbenches polled every five seconds. Each claim poll updated presence and conditionally rewrote the whole document, including when no assignment was available. Active assignments heartbeated every 20 seconds. Conditional updates retried up to 12 times, and serialized items had a 900 KiB application safety limit.

This kept the first implementation small, but coupled unrelated machines' writes and accumulated assignment history in a size-limited record. It was not load-tested for hundreds of workbenches. At 500 idle workbenches, the polling interval alone would produce approximately 100 claim requests per second, each attempting a shared-record update.

These constraints led to the independently updateable documents and per-workbench transactional queue ownership in [RFC 002](../rfcs/002-plant-service.md).

### Hosting and Production-Line Separation

The Foundry deployment combined protocol hosting, forwarded caller identity, session routing, and durable application storage. Its registration and polling operations did not invoke a model.

The discussion distinguished two responsibilities: a production-line coordinator uses MAF to decide what work to request; a shared plant service records accepted work and enforces admission, ownership, and results. Workbenches, repositories, agent roles, and workloads form line-specific bindings rather than permanent installation settings.

Foundry's documented per-session sandbox model also raised a capacity and cost question for frequent bookkeeping requests from hundreds of machines. No fleet-level cost or quota validation was performed.

RFC 002 therefore proposes a shared ASP.NET Core service on Azure App Service with Cosmos DB, separate from MAF execution. RFC 001 proposes server-supplied line configuration with explicit GitHub, agent-runtime, and telemetry integration boundaries. These are draft proposals, not deployed changes.

## Proposed Transition from the Foundry Deployment

This transition has not been executed. It is separate from the target service specification.

1. Keep the working Foundry deployment unchanged while the proposed service and installer are implemented and verified against a separate test plant.
2. Before cutover, stop submissions to the Foundry coordinator and drain or explicitly reconcile every active claim. Export registrations, queued requests, terminal records, and payloads; import with identity and content checks.
3. Inspect and retain each healthy machine's identity and journals. Explicitly map its application to the new service, grant the new API role, and verify its binding and certificate-store credential before retiring the PFX-based setup.
4. Switch each machine explicitly to the new endpoint. Never run old and new consumers for the same workbench simultaneously.
5. Retain the old store read-only for the agreed period. Rollback requires reconciliation of any work accepted by the new service; changing the URL back is not sufficient.

No deployment, directory grants, or machine changes are authorized by recording this transition.

## Implementation References

- [Workbench implementation](../src/Workbench/README.md)
- [Foundry coordinator implementation and limits](../src/Workbench/Workbench.Distributor/README.md)
- [Foundry hosted-agent session and isolation model](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
