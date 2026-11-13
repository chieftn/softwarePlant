# Rev1 Learnings: First Workbench Connection

Recorded after the first live connection on November 12, 2026. The [installation runbook](installation.md) records the current process and scripts.

## Result and Limits

The Foundry-hosted coordinator and one Windows workbench communicated successfully. The operator independently saw `boxone` online, with the expected identity and a last-contact timestamp that advanced between reads.

That is a real connection milestone. It is not an end-to-end software-production result.

| Proven in this iteration | Not yet proven |
|---|---|
| Foundry accepted and activated the published .NET coordinator without local Docker or a separate registry. | Tenant-wide repeatability or an automated Azure installation. |
| Certificate-backed application authentication reached the hosted endpoint. | Automatic enrollment, credential rotation/revocation procedures, or unattended startup. |
| Caller identity discovery and separate operator/workbench authorization worked. | A complete lifecycle for adding, removing, replacing, and upgrading machines. |
| The workbench registered and kept polling; the operator could observe its presence through the durable registry. | A deployed restart/recovery exercise, concurrent ownership checks, or two workbenches executing independently. |
| The matching host/worker package was published and installed. | Reliable release identification or coordinated coordinator/workbench upgrades. |
| No assignment was active during the connection check. | An assignment executing through WSL, MAF/Copilot, verification, commit, and PR publication. |

No PR was produced in this exercise. We did not observe a live MAF coding run.

## 1. Installation Is Lopsided

The Windows/WSL file-copy step was relatively small. Establishing permission to make that installation useful required:

1. Selecting the correct Azure tenant, subscription, resource, and project.
2. Creating a dedicated application identity.
3. Creating/exporting a certificate and uploading its public half.
4. Assigning endpoint-consumer access.
5. Authenticating as the machine and discovering Foundry's caller ID.
6. Editing coordinator environment bindings and deploying another version.
7. Creating a hosted session under that machine identity.
8. Setting local certificate credentials and starting the daemon.
9. Checking independently that polling continued.

This is operator-led configuration, not workbench self-enrollment.

The daemon already performs **registration**: it sends its machine name/capabilities and updates an existing identity-bound record. The missing capability is **enrollment**: establishing and approving that identity without manually assembling the Azure and coordinator configuration.

### Required Improvement

Provide a guided, operator-approved enrollment flow. A new workbench should be able to request admission and receive clear approval or rejection without a coordinator redeployment or an operator copying opaque caller IDs between tools.

Preserve the trust boundary: a machine must not grant itself access by claiming a workbench ID, advertising a capability, or naming a repository. Keep operator authority separate from machine authority. Handle reconnect, rotation, revocation, and replacement explicitly; do not add a complicated lifecycle framework before these concrete operations are understood.

Acceptance should be an observable outcome: install on a prepared devbox, approve enrollment, start it, and see the expected machine online. Repeating startup must reuse its identity and state rather than create duplicates.

## 2. Source, Package, Installation, and Running Process Were Conflated

The coordinator was deployed from newer code while the published workbench package still contained an earlier host. The user had the latest package supplied to them; that did not make it the latest source.

The discrepancy was reproduced directly: the repository's published executable printed only single-assignment help, while the development executable printed daemon/Foundry options. The publication step had not been kept in sync with the coordinator work.

We discovered this after doing enrollment work. The package should have been checked first.

### Required Improvement

Treat these as separate, named states:

| State | Evidence |
|---|---|
| Implemented | Source contains the capability. |
| Published | The distributable executable contains it. |
| Installed | That distributable reached the target machine. |
| Running | The target process is using the intended installation/configuration. |
| Communicating | Both sides observe successful, continuing protocol activity. |

Add visible build/package identification and compatibility information. Validate the actual release executable before asking someone to install it. A timestamp, output-directory name, source diff, or successful coordinator deployment is not a substitute.

The rollout instructions must say which components changed, which machines need an update, and which state/credentials must be preserved. They must not imply that changing Azure changes a devbox.

## 3. Startup Requires Too Much Remembered Terminal State

Early instructions were fragments depending on variables, credentials, or sessions created several steps earlier. The user could not reliably tell which command was setup, a diagnostic probe, or the daemon launch.

The successful launch was a complete PowerShell block: it named the host/certificate/session/endpoint, set the application credential explicitly, derived the WSL paths, checked the worker file, and launched the daemon. It did not depend on a remembered `$linuxHome` or password variable.

Azure PowerShell certificate sign-in was used to test the machine identity and create its session. The daemon used Azure Identity and the exported PFX through process environment variables. These are related but distinct authentication paths; testing one does not automatically configure the other.

### Required Improvement

Provide a single local startup/configuration entry point with clear prerequisites and actionable failures. It should not depend on an operator retaining a special shell window or understanding multiple credential providers.

Keep transfer, installation, and startup consistent. A ZIP is an optional transport wrapper around the same package folder, not a newly introduced installer. Do not change the presented workflow without explaining why.

The current foreground daemon and password prompt are acceptable for a connection experiment, not an unattended installation. Session creation/renewal, credential handling, process supervision, and upgrades remain unfinished.

## 4. Authentication Diagnostics Hid the Useful Evidence

Foundry endpoint access and coordinator authorization are different checks. A successful `identity` operation proves neither authorization to register a particular workbench nor permission to operate as an administrator.

We encountered a registration HTTP 403. The host displayed the status and a generic suggestion, but discarded the response body. That forced extra probes and left the original cause uncertain.

What we actually established:

- The project's consumer role existed and was assigned to the intended service principal.
- The deployed version contained the intended workbench binding.
- The session was bound to that version.
- Both the PowerShell probe and the host's own identity probe returned the expected caller ID.
- A later complete startup script succeeded.

We did **not** isolate which difference between the failed and successful launches resolved the 403. Do not record a credential mismatch or incorrect workbench argument as its proven cause.

Other concrete failures were clearer:

- The Azure CLI initially used a different tenant/subscription from the project.
- Foundry rejected an environment-variable name containing the workbench ID `box-one`; enrollment proceeded with `boxone`.
- A session log stream returned an interruption rather than application rejection details.

### Required Improvement

Expose safe server error details and correlation identifiers in the client. A failure should distinguish endpoint authentication, endpoint permission, session/version selection, coordinator identity binding, registration, and storage.

Make the host's selected authentication mechanism and non-secret identity inspectable. Do not log tokens, passwords, private certificates, or unrestricted request headers. Do not solve an unexplained denial by granting broader roles.

## 5. We Lack Useful MAF/Copilot Execution Visibility

The coordinator's current visibility is machine presence and assignment state. Runtime logs show infrastructure errors, and worker artifacts provide some progress and terminal results. None of that is yet a usable explanation of a running coding assignment.

There is some instrumentation in the implementation: the worker emits preparation, agent, verification, publication, and failure stages; the Copilot adapter streams text and records permission decisions. These facilities were not exercised in this iteration and are not a complete execution trace.

The concrete execution boundary is:

```text
Windows host -> WSL worker -> MAF AIAgent adapter -> Copilot SDK/runtime -> tools
```

MAF supplies the agent abstraction here. Copilot owns the coding tool/reasoning loop. The coordinator's registration and polling operations do not invoke a model. Monitoring Foundry alone will not reveal what happens inside the WSL worker.

### Required Improvement

Start with the questions an operator needs answered during one assignment:

| Question | Evidence needed |
|---|---|
| Did this assignment reach the intended machine? | Submission, claim, workbench identity, and timestamps. |
| Is execution preparing, waiting, running, or stuck? | Current stage, start/last-progress time, timeout and retry information. |
| Which instructions and runtime were selected? | Assignment ID, production-line revision, worker/model and package identifiers. |
| What actions did the agent attempt? | Available tool events, permission decisions, results/errors, and retry boundaries. |
| Did the work pass verification? | Exact configured checks, exit codes, and retained output. |
| Was anything published? | Commit, branch, PR URL, and independent read-back evidence. |
| What failed, and where should I look? | Correlated error and artifact references across coordinator, host, worker, MAF adapter, and Copilot. |

Capture durations and usage where the underlying SDK exposes them. Do not equate streamed agent text with a complete action log, verified correctness, or access to private model reasoning. Retain the useful action/result evidence while controlling exposure of source, prompts, and credentials.

Derive the telemetry from these concrete workloads. Do not introduce a general observability interface or another service before identifying the events the implementation can actually produce.

## 6. We Still Need the First PR

An online machine is not evidence that the worker can authenticate to Copilot, clone a repository, make a useful change, run verification, push, or create a PR.

The connection launch deliberately used `connection-check/no-work` as a placeholder allowlist and omitted agent-shell and PR-publication flags. It was not configured to manufacture a product change. The daemon is not a dedicated registration-only process, so the connection exercise also depended on not submitting assignments.

### Next Execution Proof

Use one small, real assignment with a specific acceptance criterion and verification command. Explicitly approve the target repository and the required execution/publication permissions. Check WSL Copilot, Git, and `gh` authentication under the user that actually runs the worker.

Completion requires the whole chain:

1. The operator submits to the registered workbench.
2. The daemon claims once and starts its WSL worker.
3. MAF/Copilot produces the intended change.
4. Independent verification succeeds.
5. The worker commits and creates a PR when authorized.
6. Read-back confirms the expected repository, head commit/branch, base branch, and open PR.
7. The coordinator receives the terminal result, and the artifacts explain what happened.

Do not infer any step from a conversational success message. Do not merge the PR or authorize a product release as part of this proof.

After one inspectable PR, exercise two workbenches with distinct assignments and perform the deployed interruption/restart checks. These remain separate acceptance milestones, not claims already satisfied by rev1.

## Follow-up Work

| Work | Acceptance |
|---|---|
| Self-enrollment | A prepared machine requests admission, an operator approves it, and it connects without manual caller-ID copying or coordinator redeployment. |
| Release/install consistency | The package identifies its build, matches the deployed protocol, and the installed host can report that identity before enrollment starts. |
| Startup and diagnostics | A complete supported startup path reports actionable, safe failures without relying on remembered shell state. |
| MAF/Copilot visibility | One assignment can be followed across its actual runtime/tool/verification stages using correlated evidence. |
| First PR | One explicitly authorized assignment produces a verified, inspectable PR and a coordinator-acknowledged result. |
| Recovery and a second machine | Deployed tests demonstrate correct ownership and no automatic replay across interruptions and independent workbenches. |

These are follow-up outcomes, not additional abstractions to bundle into the connection foundation.
