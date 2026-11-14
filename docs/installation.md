# Rev1 Installation and Connection Runbook

For the current Factory MSI/enrollment path, use the [live workbench installation test working document](workbench-installation-test.md).

This documents the installation process that established the first live workbench connection on November 12, 2026. It describes the current manual process, not the desired self-enrollment experience.

The verified result is a Windows workbench registered as `boxone`, polling a Foundry-hosted coordinator. No coding assignment or PR was produced during this connection exercise. See [rev1 learnings](rev1-learnings.md) for the gaps and follow-up work.

## What Runs Where

```text
Build machine
    publishes coordinator and workbench binaries
        |
        +--> Foundry project
        |       plant-coordinator -> Foundry durable state store
        |
        +--> Windows devbox
                Workbench.Host daemon
                    | outbound HTTPS to the coordinator
                    |
                    +--> Ubuntu in WSL
                            Workbench.Worker -> MAF -> Copilot SDK/runtime
```

Deploying the coordinator does not update a devbox. Publishing a workbench package does not install or start it. Installation does not start the daemon.

## Current Deployment Record

| Item | Value at the end of the connection exercise |
|---|---|
| Foundry resource | `rkesslerfactory1-resource` |
| Foundry project | `rkesslerfactory1-resourc-project` |
| Azure region | `westus3` |
| Coordinator agent | `plant-coordinator` |
| Active coordinator version | `3` |
| Durable store | `plant-main` |
| Workbench ID | `boxone` |
| WSL distribution | `Ubuntu` |
| Local repository allowlist | `connection-check/no-work`, a connection-test placeholder |
| Agent shell / PR publication | Both disabled |

Use the approved tenant, subscription, application IDs, and session ID from deployment configuration. Do not copy another machine's credential or daemon state. An existing machine should reuse its identity and local state, not repeat enrollment.

The callable endpoint for this deployment is:

```text
https://rkesslerfactory1-resource.services.ai.azure.com/api/projects/rkesslerfactory1-resourc-project/agents/plant-coordinator/endpoint/protocols/invocations
```

## Existing Scripts and Their Limits

| Script or component | What it does | What it does not do |
|---|---|---|
| `src\Workbench\publish.ps1` | Publishes the Windows host, Linux worker, bundled Copilot runtime by default, deployment script, and README into a fresh output directory. | Publish the coordinator, provision Azure, copy files to another machine, or start a daemon. |
| Published `deploy.ps1` | Checks Windows/WSL prerequisites and copies the host and worker into their installed locations. | Install dependencies, configure authentication, grant roles, create sessions, or install a Windows service. |
| `Workbench.Host.exe --daemon` | Registers machine metadata, polls for assignments, runs one assignment at a time, and reports results. | Enroll an unknown identity, grant itself access, or provision its own Foundry session. |
| Coordinator source deployment | Uses `dotnet publish`, a ZIP of its output, and Foundry's multipart REST API. | There is no checked-in Azure provisioning/deployment script for this path. The coordinator Dockerfile is an alternative, not the path used here. |
| Certificate and startup blocks below | Document the commands used during manual enrollment and startup. | These are runbook snippets, not additional checked-in installer scripts. |

## 1. Check Prerequisites

On the build machine, install the .NET 10 **SDK**. A .NET runtime alone cannot publish the projects.

```powershell
dotnet --list-sdks
```

The Windows devbox needs Windows x64, WSL with Linux x64, and the .NET 10 runtime on both sides. Check from Windows PowerShell:

```powershell
wsl.exe --list --verbose
dotnet --list-runtimes
wsl.exe --distribution Ubuntu --exec dotnet --list-runtimes
wsl.exe --distribution Ubuntu --exec git --version
wsl.exe --distribution Ubuntu --exec timeout --version
```

Both runtime checks must show `Microsoft.NETCore.App 10.x`. The distribution must be the one that will run the worker. Use non-interactive `wsl.exe --exec` checks; an interactive shell profile is not proof that the daemon will find a tool.

Before attempting coding work, also provision:

- Copilot authentication/model access for the WSL user.
- Git authentication and author identity inside WSL.
- The target product's build and test dependencies inside WSL.
- For PR publication, `gh`, GitHub CLI authentication, and repository push/PR permissions inside WSL.

The connection-only milestone does not validate these coding prerequisites. Windows Azure authentication does not provide Copilot or GitHub authentication to WSL.

## 2. Publish and Install the Matching Workbench Package

From the repository root on the build machine:

```powershell
.\src\Workbench\publish.ps1 `
    -OutputDirectory .\src\Workbench\artifacts-foundry-rev1
```

Choose a new output directory if that one is already populated. Publication rejects nonempty destinations to prevent mixed packages. The output is framework-dependent, not a self-contained .NET installation.

The default publication bundles the pinned Linux Copilot runtime. `-UseInstalledCopilot` is an alternative that requires a compatible `COPILOT_CLI_PATH` in the worker's non-interactive WSL environment; do not switch modes accidentally.

Verify the **published executable**, not just the source or a development build:

```powershell
& ".\src\Workbench\artifacts-foundry-rev1\win-x64\host\Workbench.Host.exe" --help
```

Its help must include `--daemon`, `--foundry`, `--foundry-session`, and `--invoke`.

Copy the entire output folder to the devbox using the existing file-transfer method. Its root contains:

```text
deploy.ps1
README.md
win-x64\
    host\
linux-x64\
    worker\
```

A ZIP is only an optional transfer wrapper. If using a ZIP, extract it first and run the same deployment script. It is not a different installation process.

Stop an existing daemon with Ctrl+C before replacing its binaries. On the devbox, open Windows PowerShell in the copied package folder:

```powershell
.\deploy.ps1 -Distribution Ubuntu
& "$env:LOCALAPPDATA\Workbench\Workbench.Host.exe" --help
```

Default installed locations:

| Files | Location |
|---|---|
| Windows host | `%LOCALAPPDATA%\Workbench\Workbench.Host.exe` and adjacent assemblies |
| Linux worker | WSL user's `$HOME/.local/share/workbench/worker` |
| Linux assignment workspaces | WSL user's `$HOME/.local/share/workbench/assignments` |

The script prints the actual paths. It copies package files without clearing certificate or daemon-state directories. It does not provide atomic upgrade, obsolete-file removal, or rollback management.

During rev1, the latest previously published package did not contain the newer connection code. The current development executable and the published package showed different help. A package's date or the existence of source code was not sufficient verification.

## 3. Prepare the Foundry Project and Coordinator

An Azure operator performs this section. Skip resource creation for an existing healthy deployment.

Create a Foundry resource and a project beneath it. The resource is the Azure parent; the project contains the hosted agent. Resource creation alone is not coordinator deployment.

Azure CLI must be signed into the tenant that owns the project:

```powershell
az login --tenant "<tenant-id>"
az account show --query "{subscription:id,tenant:tenantId}" -o json
```

Use explicit `--subscription` values for deployment commands. A browser signed into the correct tenant does not prove that Azure CLI is using it. A bare HTTP 409 during project creation does not identify the cause; inspect the actual error rather than deleting or recreating resources blindly.

The deploying operator needs the appropriate Foundry deployment permissions, such as Foundry Project Manager. A workbench does not need those management permissions.

### Package the Coordinator

From the repository root, using a fresh staging directory:

```powershell
$ErrorActionPreference = "Stop"
$stage = Join-Path (Get-Location).Path "coordinator-deploy-rev1"
if (Test-Path -LiteralPath $stage) {
    throw "Choose a fresh coordinator staging directory."
}
New-Item -ItemType Directory -Path $stage | Out-Null
$published = Join-Path $stage "publish"
$zip = Join-Path $stage "plant-coordinator.zip"

dotnet publish .\src\Workbench\Workbench.Distributor\Workbench.Distributor.csproj `
    -c Release -r linux-x64 --self-contained false -o $published
if ($LASTEXITCODE -ne 0) { throw "Coordinator publication failed." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($published, $zip)
Get-FileHash -LiteralPath $zip -Algorithm SHA256
```

The ZIP must contain the published files directly at its root, including `Workbench.Distributor.dll` and its runtime configuration. Do not include an extra wrapper directory. This is the Foundry code-upload format, distinct from optionally zipping the workbench package for transfer.

### Deploy or Update the Agent

The deployed definition uses:

```json
{
  "description": "Workbench plant coordinator",
  "definition": {
    "kind": "hosted",
    "cpu": "1",
    "memory": "2Gi",
    "protocol_versions": [
      { "protocol": "invocations", "version": "2.0.0" }
    ],
    "code_configuration": {
      "runtime": "dotnet_10",
      "entry_point": ["dotnet", "Workbench.Distributor.dll"],
      "dependency_resolution": "bundled"
    },
    "environment_variables": {
      "Coordinator__Mode": "Foundry",
      "Coordinator__StoreName": "plant-main"
    }
  }
}
```

Start a new deployment with no application allowlists so authenticated callers can discover their identity but cannot operate the plant. After discovery, retain the same agent/store and add approved bindings:

```text
Coordinator__Operators__0       = <verified-operator-caller-id>
Coordinator__Workbenches__boxone = <verified-workbench-caller-id>
```

Workbench bindings require at least one operator binding. Preserve existing approved bindings when updating a version.

**Use workbench IDs containing only lowercase letters and digits for this environment-variable configuration.** The application accepts hyphens in workbench IDs, but Foundry rejected the environment variable `Coordinator__Workbenches__box-one`. The successful deployment uses `boxone`. The certificate filename `box-one.pfx` is just a filename; it does not set the workbench ID.

Save the selected definition as `metadata.json` in the staging directory. The manual upload uses these API operations:

| Operation | Request |
|---|---|
| Inspect existing agent | `GET <project-endpoint>/agents/plant-coordinator?api-version=v1` |
| Create, only when absent | `POST <project-endpoint>/agents?api-version=v1` |
| Update existing agent | `POST <project-endpoint>/agents/plant-coordinator?api-version=v1` |
| Poll version | `GET <project-endpoint>/agents/plant-coordinator/versions/<version>?api-version=v1` |
| Verify uploaded code | `GET <project-endpoint>/agents/plant-coordinator/code:download?api-version=v1&agent_version=<version>` |

The following upload block runs in Windows PowerShell with `$stage`, `$zip`, and the saved metadata from above. Set the identifiers explicitly. Set `$create` to `$true` only after a GET confirms the agent is absent:

```powershell
Add-Type -AssemblyName System.Net.Http
$subscriptionId = "<subscription-id>"
$projectEndpoint = "https://<account>.services.ai.azure.com/api/projects/<project>"
$agentName = "plant-coordinator"
$create = $false
$metadataPath = Join-Path $stage "metadata.json"

$token = az account get-access-token --subscription $subscriptionId `
    --resource "https://ai.azure.com" --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw "Could not obtain an operator token."
}

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(3)
$http.DefaultRequestHeaders.Authorization = (
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token.Trim())
)
$http.DefaultRequestHeaders.Accept.ParseAdd("application/json")
$token = $null
$multipart = [System.Net.Http.MultipartFormDataContent]::new()

try {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw
    $multipart.Add(
        [System.Net.Http.StringContent]::new(
            $metadata, [System.Text.Encoding]::UTF8, "application/json"),
        "metadata")
    $code = [System.Net.Http.ByteArrayContent]::new(
        [System.IO.File]::ReadAllBytes($zip))
    $code.Headers.ContentType = (
        [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/zip")
    )
    $multipart.Add($code, "code", "plant-coordinator.zip")

    $url = if ($create) {
        "$projectEndpoint/agents?api-version=v1"
    } else {
        "$projectEndpoint/agents/${agentName}?api-version=v1"
    }
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, $url)
    $request.Headers.Add(
        "x-ms-code-zip-sha256",
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant())
    if ($create) { $request.Headers.Add("x-ms-agent-name", $agentName) }
    $request.Content = $multipart

    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (!$response.IsSuccessStatusCode) {
        throw "Foundry returned HTTP $([int]$response.StatusCode): $body"
    }
    $body | Set-Content -LiteralPath (Join-Path $stage "deployment-response.json")
    $body
}
finally {
    $multipart.Dispose()
    $http.Dispose()
}
```

Read the returned version from `versions.latest.version`, then poll that exact version until `status` is `active`. Do not invoke a version still provisioning or blindly repeat a POST after an uncertain response. Inspect the agent/version first.

```powershell
$version = "<returned-version>"
az rest --method get --subscription $subscriptionId `
    --resource "https://ai.azure.com" `
    --url "$projectEndpoint/agents/$agentName/versions/${version}?api-version=v1"
```

On failure, retain the version's error details. After activation, verify the returned environment bindings and compare the SHA-256 of downloaded code with the uploaded ZIP.

The actual rev1 sequence was version 1 for identity discovery, version 2 for operator authorization, and version 3 for workbench authorization. These numbers record this installation; they are not fixed version requirements for another plant.

No local Docker daemon or separate Azure Container Registry was used. Foundry hosted the published .NET package directly. See Microsoft's [source-code deployment procedure](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent-code).

## 4. Enroll the Workbench Identity Manually

Perform certificate creation on the actual Windows workbench, not WSL, Cloud Shell, or the build machine. Keep operator and workbench identities separate.

### Register the Application and Certificate

In the Entra directory that owns the Foundry project:

1. Open App registrations and create an application for this workbench.
2. Select accounts in this organizational directory only; leave redirect URI blank.
3. Record its Application (client) ID and tenant ID.
4. Do not add a client secret or Microsoft Graph permissions for this connection.

For the first workbench, the application display name was `rkesslerwsl`. The display name is a label, not the identifier used to authorize the machine.

Create and export a certificate in Windows PowerShell. This is for a **new enrollment only**; do not replace an existing working certificate:

```powershell
$ErrorActionPreference = "Stop"
$identityDir = "$env:LOCALAPPDATA\Workbench\identity"
New-Item -ItemType Directory -Path $identityDir -Force | Out-Null

if (Test-Path "$identityDir\box-one.pfx") {
    throw "An existing certificate file must not be overwritten."
}

$password = Read-Host "Choose a PFX password" -AsSecureString
$cert = New-SelfSignedCertificate `
    -Subject "CN=workbench-box-one" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(1)

Export-PfxCertificate -Cert $cert `
    -FilePath "$identityDir\box-one.pfx" -Password $password | Out-Null
Export-Certificate -Cert $cert `
    -FilePath "$identityDir\box-one.cer" | Out-Null
```

In the app registration, open Certificates & secrets, select Certificates, and upload **`box-one.cer`**. Never upload the PFX.

Protect the PFX, its password, and the Windows user account holding the private key. Do not put credentials in the repository, package, assignments, screenshots, or chat. Record the certificate's expiration and arrange renewal; no automatic rotation is implemented.

### Grant Endpoint Access

An Azure operator assigns **Foundry Agent Consumer** to the application's service principal at the **project** scope. Do not substitute Contributor, Owner, or a broad AI role.

If the portal's role picker does not show it, inspect the actual subscription role definition rather than assuming it is unavailable. The verified built-in role ID is:

```text
eed3b665-ab3a-47b6-8f48-c9382fb1dad6
```

Its data action is:

```text
Microsoft.CognitiveServices/accounts/AIServices/endpoints/interact/action
```

Equivalent Azure CLI commands, run as the operator:

```powershell
$subscriptionId = "<subscription-id>"
$resourceGroup = "<resource-group>"
$accountName = "<foundry-account>"
$projectName = "<foundry-project>"
$applicationId = "<workbench-application-client-id>"
$roleId = "eed3b665-ab3a-47b6-8f48-c9382fb1dad6"
$scope = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.CognitiveServices/accounts/$accountName/projects/$projectName"

$principalId = az ad sp show --id $applicationId --query id -o tsv
if ($LASTEXITCODE -ne 0) { throw "Could not resolve the workbench service principal." }

az role assignment list --subscription $subscriptionId `
    --scope $scope --all `
    --query "[?principalId=='$principalId' && ends_with(roleDefinitionId, '$roleId')]"
```

For inspection, use the returned assignment records to check principal, role definition, and exact scope. If no matching assignment exists, create it:

```powershell
az role assignment create --subscription $subscriptionId `
    --assignee-object-id $principalId `
    --assignee-principal-type ServicePrincipal `
    --role $roleId --scope $scope
if ($LASTEXITCODE -ne 0) { throw "Workbench role assignment failed." }
```

Do not grant extra permissions to bypass an unexplained 403.

### Authenticate and Discover the Actual Caller ID

Use Azure PowerShell's `Az.Accounts` module on the workbench for these manual setup calls. If `Connect-AzAccount` is unavailable, install that module for the current user before continuing:

```powershell
Install-Module Az.Accounts -Scope CurrentUser -Repository PSGallery
```

Sign in using the certificate already in the current Windows user's certificate store. This is separate from the operator's Azure CLI login:

```powershell
$tenantId = "<tenant-id>"
$applicationId = "<workbench-application-client-id>"
$publicCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    "$env:LOCALAPPDATA\Workbench\identity\box-one.cer")

Connect-AzAccount -ServicePrincipal `
    -Tenant $tenantId -ApplicationId $applicationId `
    -CertificateThumbprint $publicCert.Thumbprint `
    -Scope Process -SkipContextPopulation
```

In that same PowerShell window:

```powershell
$project = "https://<account>.services.ai.azure.com/api/projects/<project>"
$access = Get-AzAccessToken -ResourceUrl "https://ai.azure.com"
$bearer = if ($access.Token -is [System.Security.SecureString]) {
    [System.Net.NetworkCredential]::new("", $access.Token).Password
} else {
    $access.Token
}
$workbenchHeaders = @{ Authorization = "Bearer $bearer" }

Invoke-RestMethod `
    -Uri "$project/agents/plant-coordinator/endpoint/protocols/invocations?api-version=v1" `
    -Method Post -Headers $workbenchHeaders `
    -ContentType "application/json" `
    -Body '{"schemaVersion":"0.1","operation":"identity"}'
```

Record only the returned `principalId`. Do not print or share `$access`, `$bearer`, or `$workbenchHeaders`.

The coordinator uses Foundry's caller ID, not an assumed application ID or object ID. In this deployment the discovered caller ID matched the service principal object ID; that observation is not a contract.

An operator follows the same `identity` protocol under their own identity when establishing an operator binding.

### Authorize the Workbench Inside the Coordinator

Endpoint access and coordinator access are separate checks. Add the discovered caller ID as:

```text
Coordinator__Workbenches__boxone = <discovered-principal-id>
```

Retain the operator binding and `plant-main`, then deploy a new coordinator version using section 3. Wait until it is active and verify the binding.

The current process requires this redeployment for enrollment. The daemon does not perform it itself. Do not try to rebind an already registered workbench to another identity merely by editing this setting.

## 5. Create the Workbench's Hosted Session

Run this as the **workbench**, using the certificate-authenticated PowerShell context from section 4, not the operator's identity. Refresh the token first; bearer tokens are not permanent configuration:

```powershell
$project = "https://<account>.services.ai.azure.com/api/projects/<project>"
$coordinatorVersion = "<active-version-containing-the-workbench-binding>"
$access = Get-AzAccessToken -ResourceUrl "https://ai.azure.com"
$bearer = if ($access.Token -is [System.Security.SecureString]) {
    [System.Net.NetworkCredential]::new("", $access.Token).Password
} else {
    $access.Token
}
$workbenchHeaders = @{ Authorization = "Bearer $bearer" }
$sessionBody = @{
    version_indicator = @{
        type = "version_ref"
        agent_version = $coordinatorVersion
    }
} | ConvertTo-Json -Depth 4

$session = Invoke-RestMethod `
    -Uri "$project/agents/plant-coordinator/endpoint/sessions?api-version=v1" `
    -Method Post -Headers $workbenchHeaders `
    -ContentType "application/json" -Body $sessionBody

$session | Select-Object agent_session_id, status
```

Retain the returned session ID in the workbench's local configuration. Wait for an active session before the initial connection. Use a session belonging to this workbench identity and containing the intended coordinator version.

The daemon passes this ID as the `agent_session_id` query parameter on every invocation. Reuse it rather than creating a sandbox every five seconds. A session pinned to an earlier version does not acquire new authorization just because a newer coordinator version was deployed; create an appropriately versioned session when needed.

Polling keeps hosted compute active and incurs charges. A hosted session is not the Windows daemon, and creating it does not start local execution. Sessions also have a finite platform lifetime; automatic session creation/replacement is not implemented in the daemon. See [Foundry session management](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-sessions).

## 6. Start the Daemon

This complete block runs in a fresh Windows PowerShell window on the devbox. Fill in the three deployment-specific placeholders before running it: tenant ID, application client ID, and the workbench's session ID. It does not depend on the earlier `$password`, `$project`, or token variables.

For the recorded installation, the endpoint, distribution, certificate filename, and `boxone` ID below are already correct.

```powershell
$ErrorActionPreference = "Stop"

$hostPath = "$env:LOCALAPPDATA\Workbench\Workbench.Host.exe"
$certificatePath = "$env:LOCALAPPDATA\Workbench\identity\box-one.pfx"
$distribution = "Ubuntu"
$sessionId = "<workbench-session-id>"
$endpoint = "https://rkesslerfactory1-resource.services.ai.azure.com/api/projects/rkesslerfactory1-resourc-project/agents/plant-coordinator/endpoint/protocols/invocations"

if (!(Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    throw "Workbench host is missing: $hostPath"
}
if (!(Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "Workbench certificate is missing: $certificatePath"
}

$env:AZURE_TENANT_ID = "<tenant-id>"
$env:AZURE_CLIENT_ID = "<workbench-application-client-id>"
$env:AZURE_CLIENT_CERTIFICATE_PATH = $certificatePath
$env:AZURE_CLIENT_SECRET = $null
$env:AZURE_TOKEN_CREDENTIALS = "EnvironmentCredential"

$password = Read-Host "PFX password" -AsSecureString
$env:AZURE_CLIENT_CERTIFICATE_PASSWORD = (
    [System.Net.NetworkCredential]::new("", $password)
).Password

try {
    $linuxHome = (
        & wsl.exe --distribution $distribution --exec printenv HOME |
            Out-String
    ).Trim()
    if ($LASTEXITCODE -ne 0 -or !$linuxHome.StartsWith("/")) {
        throw "Could not determine the Ubuntu home directory."
    }

    $workerPath = "$linuxHome/.local/share/workbench/worker/Workbench.Worker.dll"
    $workspaceRoot = "$linuxHome/.local/share/workbench/assignments"
    & wsl.exe --distribution $distribution --exec test -f $workerPath
    if ($LASTEXITCODE -ne 0) {
        throw "The deployed worker is missing: $workerPath"
    }

    $launchArguments = @(
        "--daemon"
        "--foundry"
        "--foundry-session", $sessionId
        "--distributor", $endpoint
        "--workbench", "boxone"
        "--state", "$env:LOCALAPPDATA\Workbench\daemon"
        "--repositories", "connection-check/no-work"
        "--distro", $distribution
        "--worker-path", $workerPath
        "--workspace-root", $workspaceRoot
        "--output", "$env:LOCALAPPDATA\Workbench\results"
    )

    & $hostPath @launchArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Workbench exited with code $LASTEXITCODE."
    }
}
finally {
    $env:AZURE_CLIENT_CERTIFICATE_PASSWORD = $null
}
```

`AZURE_TOKEN_CREDENTIALS=EnvironmentCredential` makes the current host use its configured application credential rather than a developer-login fallback. These variables are process-scoped; the PFX password is available to the child host while running and cleared from the parent environment when it exits. This is not unattended credential management.

Expected output:

```text
Workbench boxone registered; one assignment at a time.
```

Leave the window running. Quiet output while no work is queued is normal. The daemon polls every five seconds.

The repository value is deliberately a placeholder for a connection-only exercise; it is not an operating-system security boundary. Do not submit assignments to it. There is no registration-only mode: this daemon will accept queued assignments that pass its configured policy. Do not queue work during the connection exercise.

For real work, explicitly select the allowed repositories and restart the daemon with that allowlist. Add `--allow-shell` or `--publish-pr` only when intentionally authorizing those capabilities. Submission must request compatible permissions as well. Verification commands are trusted operator-supplied executable code even without agent shell permission.

## 7. Verify from the Coordinator

In a separate operator terminal, use the operator's Azure CLI context, not the workbench application's PowerShell context:

```powershell
$subscriptionId = "<subscription-id>"
$project = "https://rkesslerfactory1-resource.services.ai.azure.com/api/projects/rkesslerfactory1-resourc-project"
$token = az account get-access-token --subscription $subscriptionId `
    --resource "https://ai.azure.com" --query accessToken -o tsv
if ($LASTEXITCODE -ne 0) { throw "Could not obtain an operator token." }

Invoke-RestMethod `
    -Uri "$project/agents/plant-coordinator/endpoint/protocols/invocations?api-version=v1" `
    -Method Post -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body '{"schemaVersion":"0.1","operation":"workbenches"}' |
    ConvertTo-Json -Depth 10
```

Check `boxone`, the expected principal, `presence: online`, and no active assignment. Repeat after more than five seconds and verify `lastSeen` advances. Registration success alone does not prove continuing communication.

This was the independent confirmation used for rev1. It did not exercise the WSL coding worker.

## Stop, Restart, and Inspect

Ctrl+C requests cooperative shutdown. Restart using the same installed identity and daemon-state directory. There is no automatic Windows service, scheduled startup, certificate renewal, or daemon/session supervisor.

Do not delete `pending.json`, `claim-intent.json`, or result journals to bypass an interrupted assignment. Do not run two daemons under one workbench identity or copy daemon state to a different machine. Inspect WSL execution and Git/PR state before reconciling interrupted work. An offline machine does not automatically release a claim.

Current monitoring is split across:

| Location | Available evidence |
|---|---|
| Coordinator `workbenches` operation | Registration, presence, last contact, active assignment |
| Coordinator `get` operation with an assignment ID | Assignment status and terminal result |
| Foundry session logs | Startup, rejected operations, runtime/storage errors; retrieval depends on session state and permissions |
| `%LOCALAPPDATA%\Workbench\results` | Per-assignment `request.json`, `events.jsonl`, `worker-stderr.log`, `result.json` |
| WSL assignment directory | Checkout, request, verification/result data, and PR-body artifact |

There is no consolidated monitoring UI or complete MAF/Copilot execution trace.

## Troubleshooting Boundaries

| Symptom | Check before changing anything |
|---|---|
| .NET runtime missing | Check Windows and the selected WSL distribution separately. Deployment does not install either. |
| Help only shows `--assignment` | Compare the actual published and installed executables with the current source. Do not diagnose from a folder name or date alone. |
| Azure lists the wrong resources or denies discovery | Verify CLI tenant/subscription separately from the browser login. |
| HTTP 401 | Inspect token acquisition and the `https://ai.azure.com` audience. |
| HTTP 403 | Separate endpoint RBAC, actual caller identity, session/version binding, and coordinator workbench/operator allowlists. Do not broaden grants speculatively. |
| Identity works, registration fails | Compare the host's own identity, exact workbench ID, and the configuration of the session's version. `boxone` and `box-one` are different IDs. |
| Environment-variable name rejected | Use an ID compatible with Foundry's environment-variable naming rules; the hyphenated example failed in this deployment. |
| Session log stream yields no application log | It is not proof that the application did not receive a request. Rev1 log retrieval returned an interrupted stream. |
| Daemon stops on interrupted work | Reconcile the existing assignment and external effects; do not erase journals and replay it. |

`DistributorClient` currently reports an unsuccessful response's HTTP status but discards the server's error body. If that prevents diagnosis, reproduce the narrow failing operation with an authenticated REST call and retain its response body. An identity-only probe does not test registration or coding execution.

The initial registration 403 was not conclusively attributed. Both the PowerShell probe and host probe returned the expected principal. The later complete startup script succeeded; that does not prove which difference resolved the earlier failure.

## Installation Completion Checklist

- The installed package, not only the source, supports Foundry daemon mode.
- The workbench has its own certificate-backed application identity.
- Endpoint consumer access and coordinator workbench authorization are both configured.
- The session belongs to that identity and uses the intended coordinator version.
- The daemon reports successful registration.
- The operator sees `online` and an advancing `lastSeen`.
- No coding assignment has been inferred from connection success.

Creating a PR is a separate acceptance milestone. Follow the [workbench execution instructions](../src/Workbench/README.md) only after selecting a real repository, validating WSL credentials/tools, and explicitly authorizing execution/publication.
