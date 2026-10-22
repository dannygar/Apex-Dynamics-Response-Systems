#!/usr/bin/env pwsh
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
#Requires -Version 7.4

<#
.SYNOPSIS
    Provisions and deploys the Evaluation workspace and its Foundry hosted judge agent.
.DESCRIPTION
    Idempotent end-to-end deployment:
    1. Validates the active Azure CLI tenant and subscription.
    2. Deploys the foundation (identity, registry, SQL, Blob, VNet, private endpoints, environment).
    3. Imports ClamAV and builds the web, worker, and judge images in ACR (cloud build, no local Docker).
    4. Deploys one Container App per image plus the migration job.
    5. Grants the managed identity database roles, runs migrations, and bootstraps the first administrator
       through a short-lived operator job inside the VNet (SQL accepts private-endpoint traffic only).
    6. Creates or versions the agent-eval Foundry hosted agent from the judge image and grants its
       agent identity access to the judge model.
    7. Registers the production MSAL redirect URI and checks /health.
    Every Bicep deployment is preceded by what-if.
.PARAMETER SubscriptionId
    Target subscription.
.PARAMETER TenantId
    Entra tenant of the subscription and app registrations.
.PARAMETER ResourceGroupName
    Resource group for the workload.
.PARAMETER Location
    Azure region.
.PARAMETER SpaClientId
    Application (client) ID of the SPA registration.
.PARAMETER ApiAppId
    Application (client) ID of the API registration.
.PARAMETER TeamName
    Name of the platform administrator team created or flagged during bootstrap.
.PARAMETER AdminObjectId
    Entra object ID of the first team administrator.
.PARAMETER ImageTag
    Tag applied to built images.
.PARAMETER SkipBuild
    Reuse images already pushed with -ImageTag instead of rebuilding.
.EXAMPLE
    ./Deploy-Evaluation.ps1 -SubscriptionId <sub> -TenantId <tenant> -SpaClientId <spa> -ApiAppId <api> -AdminObjectId <oid>
.NOTES
    Run with an Azure CLI profile signed in to the target tenant. Creates billable resources.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $false)]
    [string]$ResourceGroupName = 'rg-apex-eval',

    [Parameter(Mandatory = $false)]
    [string]$Location = 'eastus2',

    [Parameter(Mandatory = $true)]
    [string]$SpaClientId,

    [Parameter(Mandatory = $true)]
    [string]$ApiAppId,

    [Parameter(Mandatory = $false)]
    [string]$TeamName = 'Apex Workshop - Administrator',

    [Parameter(Mandatory = $true)]
    [string]$AdminObjectId,

    [Parameter(Mandatory = $false)]
    [string]$ImageTag = (Get-Date -Format 'yyyyMMddHHmmss'),

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$Template = Join-Path $RepoRoot 'infra/main.bicep'
$Parameters = Join-Path $RepoRoot 'infra/main.parameters.json'
$ClamAvSource = 'docker.io/clamav/clamav:1.4'
$OpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

#region Functions

function Invoke-Az {
    # Simple (non-advanced) function so az flags such as --name pass through $args untouched.
    $output = az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0..2] -join ' ') failed (exit $LASTEXITCODE)." }
    return $output
}

function Invoke-Deployment {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][string[]]$Overrides, [Parameter(Mandatory = $true)][string]$Name)
    $common = @('--resource-group', $ResourceGroupName, '--template-file', $Template,
        '--parameters', "@$Parameters", '--parameters') + $Overrides
    Write-Host "🔎 what-if: $Name" -ForegroundColor Cyan
    Invoke-Az deployment group what-if --name $Name --result-format ResourceIdOnly --no-pretty-print @common | Out-Null
    Write-Host "🚀 deploy: $Name" -ForegroundColor Cyan
    $result = Invoke-Az deployment group create --name $Name @common --query properties.outputs -o json
    return ($result | Out-String | ConvertFrom-Json)
}

function Wait-JobExecution {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$JobName, [Parameter(Mandatory = $true)][string]$ExecutionName)
    $deadline = (Get-Date).AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        $status = Invoke-Az containerapp job execution show --name $JobName --resource-group $ResourceGroupName `
            --job-execution-name $ExecutionName --query properties.status -o tsv
        if ($status -eq 'Succeeded') { return }
        if ($status -in @('Failed', 'Stopped', 'Degraded')) { throw "Job $JobName execution $ExecutionName ended with status $status." }
        Start-Sleep -Seconds 15
    }
    throw "Job $JobName execution $ExecutionName timed out."
}

function Invoke-OperatorSql {
    <#
    .SYNOPSIS
        Runs one T-SQL batch as the deploying user from inside the VNet.
    .DESCRIPTION
        SQL accepts only private-endpoint traffic (tenant policy), so the batch runs in a short-lived
        Container Apps job on the workload environment. The caller's Entra token (about 1 hour lifetime)
        is passed as a job secret through a temporary spec file, never on a command line, and the job is
        deleted afterwards.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Outputs,
        [Parameter(Mandatory = $true)][string]$Sql,
        [Parameter(Mandatory = $false)][hashtable]$Parameters = @{}
    )
    $runner = @'
$ErrorActionPreference = 'Stop'
$sql = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:SQL_SCRIPT_B64))
$values = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:SQL_PARAMS_B64)) | ConvertFrom-Json
$connection = [System.Data.SqlClient.SqlConnection]::new("Server=tcp:$($env:SQL_SERVER),1433;Database=$($env:SQL_DB);Encrypt=True;TrustServerCertificate=False;Connection Timeout=90;")
$connection.AccessToken = $env:SQL_TOKEN
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = 120
    foreach ($p in $values.PSObject.Properties) { [void]$command.Parameters.AddWithValue($p.Name, [string]$p.Value) }
    [void]$command.ExecuteNonQuery()
    Write-Output 'operator-sql: ok'
}
finally { $connection.Dispose() }
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($runner))
    $token = Invoke-Az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    $jobName = "azops$(($Outputs.identityName.value).Substring(4))"
    $spec = @{
        location   = $Outputs.location.value
        properties = @{
            environmentId = $Outputs.environmentId.value
            configuration = @{
                triggerType         = 'Manual'
                replicaTimeout      = 600
                replicaRetryLimit   = 0
                manualTriggerConfig = @{ parallelism = 1; replicaCompletionCount = 1 }
                secrets             = @(@{ name = 'sqltoken'; value = $token })
            }
            template      = @{
                containers = @(@{
                        name      = 'operator'
                        image     = 'mcr.microsoft.com/powershell:lts-7.4-ubuntu-22.04'
                        command   = @('pwsh', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
                        resources = @{ cpu = 0.25; memory = '0.5Gi' }
                        env       = @(
                            @{ name = 'SQL_TOKEN'; secretRef = 'sqltoken' }
                            @{ name = 'SQL_SERVER'; value = $Outputs.sqlServerFqdn.value }
                            @{ name = 'SQL_DB'; value = $Outputs.databaseName.value }
                            @{ name = 'SQL_SCRIPT_B64'; value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Sql)) }
                            @{ name = 'SQL_PARAMS_B64'; value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Parameters | ConvertTo-Json -Compress))) }
                        )
                    })
            }
        }
    }
    $specFile = Join-Path ([IO.Path]::GetTempPath()) "apex-operator-$([guid]::NewGuid().ToString('N')).yaml"
    try {
        $spec | ConvertTo-Json -Depth 10 | Set-Content -Path $specFile -Encoding utf8NoBOM
        Invoke-Az containerapp job create --name $jobName --resource-group $ResourceGroupName --yaml $specFile -o none
        Remove-Item $specFile -Force
        $execution = Invoke-Az containerapp job start --name $jobName --resource-group $ResourceGroupName --query name -o tsv
        Wait-JobExecution -JobName $jobName -ExecutionName $execution
    }
    finally {
        if (Test-Path $specFile) { Remove-Item $specFile -Force }
        az containerapp job delete --name $jobName --resource-group $ResourceGroupName --yes -o none 2>$null
    }
}

function Invoke-AcrBuild {
    <#
    .SYNOPSIS
        Builds images in ACR from one uploaded source archive.
    .DESCRIPTION
        'az acr build' can hang indefinitely uploading its context over slow links. This packs the context once
        (honoring the same exclusions as .dockerignore), uploads it with bounded retries, schedules one ACR run per
        image, and waits for all of them. Transient registry pull errors are retried once per image.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Registry,
        [Parameter(Mandatory = $true)][object[]]$Images
    )
    $base = Invoke-Az acr show --name $Registry --query id -o tsv
    $archive = Join-Path ([IO.Path]::GetTempPath()) "apex-context-$([guid]::NewGuid().ToString('N')).tar.gz"
    $exclude = @('node_modules', 'bin', 'obj', '.venv', '__pycache__', '.pytest_cache', '.ruff_cache', '.git', '.github',
        '.copilot-tracking', '.azure', '.vscode', 'dist', 'docs', 'infra', 'results', 'local', '*.db', 'judge.local.yaml',
        '.env.local') | ForEach-Object { "--exclude=$_" }
    Push-Location $RepoRoot
    try { tar -czf $archive @exclude . ; if ($LASTEXITCODE -ne 0) { throw 'Packing the build context failed.' } }
    finally { Pop-Location }
    try {
        $pending = @($Images)
        for ($round = 1; $round -le 2 -and $pending.Count -gt 0; $round++) {
            $upload = Invoke-Az rest --method post --url "https://management.azure.com$base/listBuildSourceUploadUrl?api-version=2019-06-01-preview" -o json | Out-String | ConvertFrom-Json
            $uploaded = $false
            foreach ($attempt in 1..3) {
                try {
                    Invoke-WebRequest -Method Put -Uri $upload.uploadUrl -InFile $archive -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -TimeoutSec 1800 | Out-Null
                    $uploaded = $true; break
                }
                catch { Write-Host "  context upload attempt $attempt failed; retrying" -ForegroundColor Yellow; Start-Sleep -Seconds 10 }
            }
            if (-not $uploaded) { throw 'Build context upload failed.' }
            $runs = foreach ($image in $pending) {
                $request = @{
                    type = 'DockerBuildRequest'; imageNames = @($image.Name); isPushEnabled = $true; noCache = $false
                    dockerFilePath = $image.Dockerfile; platform = @{ os = 'Linux'; architecture = 'amd64' }
                    sourceLocation = $upload.relativePath; timeout = 3600
                    arguments = @($image.Arguments.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value; isSecret = $false } })
                }
                $file = New-TemporaryFile
                try {
                    $request | ConvertTo-Json -Depth 6 | Set-Content -Path $file -Encoding utf8NoBOM
                    $run = Invoke-Az rest --method post --url "https://management.azure.com$base/scheduleRun?api-version=2019-06-01-preview" `
                        --body "@$file" --headers Content-Type=application/json -o json | Out-String | ConvertFrom-Json
                }
                finally { Remove-Item $file -Force }
                [pscustomobject]@{ Image = $image; RunId = $run.properties.runId }
            }
            $deadline = (Get-Date).AddMinutes(45)
            do {
                Start-Sleep -Seconds 20
                $states = foreach ($r in $runs) { [pscustomobject]@{ Run = $r; Status = (Invoke-Az acr task show-run --registry $Registry --run-id $r.RunId --query status -o tsv) } }
            } while (@($states | Where-Object { $_.Status -in @('Queued', 'Started', 'Running') }).Count -gt 0 -and (Get-Date) -lt $deadline)
            foreach ($s in $states) { Write-Host "  $($s.Run.Image.Name): $($s.Status) (run $($s.Run.RunId))" }
            $pending = @($states | Where-Object { $_.Status -ne 'Succeeded' } | ForEach-Object { $_.Run.Image })
        }
        if ($pending.Count -gt 0) { throw "Image builds failed: $(($pending | ForEach-Object Name) -join ', ')." }
    }
    finally { Remove-Item $archive -Force -ErrorAction SilentlyContinue }
}

function Grant-DirectoryInvitePermissions {
    # The API invites guests through Microsoft Graph with its managed identity (application permissions).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$PrincipalId)
    $token = Invoke-Az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
    $headers = @{ Authorization = "Bearer $token" }
    $graph = (Invoke-RestMethod -Headers $headers -Uri "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'&`$select=id,appRoles").value[0]
    $assigned = (Invoke-RestMethod -Headers $headers -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$PrincipalId/appRoleAssignments").value
    foreach ($permission in 'User.Invite.All', 'User.ReadBasic.All') {
        $role = $graph.appRoles | Where-Object { $_.value -eq $permission -and $_.allowedMemberTypes -contains 'Application' }
        if ($assigned | Where-Object { $_.appRoleId -eq $role.id -and $_.resourceId -eq $graph.id }) { continue }
        Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
            -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$PrincipalId/appRoleAssignments" `
            -Body (@{ principalId = $PrincipalId; resourceId = $graph.id; appRoleId = $role.id } | ConvertTo-Json) | Out-Null
    }
}

function Invoke-Foundry {
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $false)][object]$Body
    )
    $token = Invoke-Az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    $request = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $token" }; ContentType = 'application/json' }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 10) }
    return Invoke-RestMethod @request
}

function Find-IdentityId {
    # Walks the agent JSON for identity object IDs; the field name varies across API revisions.
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][AllowNull()][object]$Node)
    $found = [System.Collections.Generic.List[string]]::new()
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $Node.PSObject.Properties) {
            if ($p.Name -match '^(agentIdentityId|agent_identity_id|principal_id|principalId)$' -and $p.Value -is [string] -and
                $p.Value -match '^[0-9a-fA-F-]{36}$') { $found.Add($p.Value) }
            else { foreach ($v in (Find-IdentityId -Node $p.Value)) { $found.Add($v) } }
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) { foreach ($v in (Find-IdentityId -Node $item)) { $found.Add($v) } }
    }
    return $found.ToArray()
}

function Publish-JudgeAgent {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][string]$ProjectEndpoint, [Parameter(Mandatory = $true)][string]$Image)
    $settings = Get-Content $Parameters -Raw | ConvertFrom-Json
    $name = $settings.parameters.foundryAgentName.value
    $definition = @{
        kind                    = 'hosted'
        container_configuration = @{ image = $Image }
        cpu                     = '1'
        memory                  = '2Gi'
        protocol_versions       = @(@{ protocol = 'invocations'; version = '2.0.0' })
        # Same values as the internal judge app so both report the same contract version.
        environment_variables   = @{
            AZURE_OPENAI_ENDPOINT    = $settings.parameters.judgeEndpoint.value
            AZURE_OPENAI_DEPLOYMENT  = $settings.parameters.judgeDeployment.value
            AZURE_OPENAI_API_VERSION = $settings.parameters.judgeApiVersion.value
            JUDGE_SAMPLES            = [string]$settings.parameters.judgeSamples.value
        }
    }
    $base = "$ProjectEndpoint/agents"
    $exists = $true
    try { Invoke-Foundry -Method GET -Uri "$base/$name`?api-version=v1" | Out-Null } catch { $exists = $false }
    Write-Host "🤖 $(if ($exists) { 'Versioning' } else { 'Creating' }) Foundry hosted agent '$name'" -ForegroundColor Cyan
    $created = if ($exists) {
        Invoke-Foundry -Method POST -Uri "$base/$name/versions?api-version=v1" -Body @{ definition = $definition }
    }
    else {
        Invoke-Foundry -Method POST -Uri "$base`?api-version=v1" -Body @{ name = $name; definition = $definition }
    }
    $version = $created.version ?? $created.versions.latest.version
    if (-not $version) { throw 'Foundry did not return an agent version.' }
    $deadline = (Get-Date).AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        $info = Invoke-Foundry -Method GET -Uri "$base/$name/versions/$version`?api-version=v1"
        if ($info.status -eq 'active') { break }
        if ($info.status -eq 'failed') { throw "Hosted agent version $version failed: $($info.error.code) $($info.error.message)" }
        Start-Sleep -Seconds 10
    }
    if ($info.status -ne 'active') { throw "Hosted agent version $version did not become active." }
    $agent = Invoke-Foundry -Method GET -Uri "$base/$name`?api-version=v1"
    return [pscustomobject]@{ Name = $name; Version = $version; Identities = @((Find-IdentityId -Node $agent) + (Find-IdentityId -Node $info) | Sort-Object -Unique) }
}

function Grant-JudgeModelAccess {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string[]]$PrincipalIds)
    $settings = Get-Content $Parameters -Raw | ConvertFrom-Json
    $scope = Invoke-Az cognitiveservices account show --name $settings.parameters.judgeAccountName.value `
        --resource-group $settings.parameters.judgeResourceGroupName.value --query id -o tsv
    $graphToken = Invoke-Az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
    foreach ($principal in $PrincipalIds) {
        # Agent identity blueprints share the ID shape but can't hold Azure RBAC roles; grant agent identities only.
        $type = (Invoke-RestMethod -Headers @{ Authorization = "Bearer $graphToken" } `
                -Uri "https://graph.microsoft.com/v1.0/directoryObjects/$principal").'@odata.type'
        if ($type -ne '#microsoft.graph.agentIdentity') { Write-Host "  skip $principal ($type)"; continue }
        # Avoid parentheses in --query: az.cmd on Windows misparses them.
        $existing = @(Invoke-Az role assignment list --assignee $principal --scope $scope --role $OpenAiUserRoleId --query "[].id" -o tsv)
        if ($existing.Count -eq 0 -or -not $existing[0]) {
            Invoke-Az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal `
                --role $OpenAiUserRoleId --scope $scope -o none
        }
    }
}

#endregion Functions

#region Main Execution

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $account = Invoke-Az account show -o json | Out-String | ConvertFrom-Json
        if ($account.tenantId -ne $TenantId -or $account.id -ne $SubscriptionId) {
            throw "Active Azure CLI context ($($account.tenantId)/$($account.id)) is not the target tenant/subscription."
        }
        if ((Invoke-Az group exists --name $ResourceGroupName) -ne 'true') {
            Invoke-Az group create --name $ResourceGroupName --location $Location --tags workload=apex-evaluation -o none
        }

        $foundation = Invoke-Deployment -Name 'apex-eval-foundation' -Overrides @('shouldDeployApps=false')
        $registry = $foundation.registryName.value
        $loginServer = $foundation.registryLoginServer.value

        $clamavTag = 'clamav:1.4'
        $existing = az acr repository show-tags --name $registry --repository clamav -o tsv 2>$null
        if ($existing -notcontains '1.4') {
            Write-Host '📦 Importing ClamAV' -ForegroundColor Cyan
            Invoke-Az acr import --name $registry --source $ClamAvSource --image $clamavTag -o none
        }
        if (-not $SkipBuild) {
            Write-Host "📦 Building images ($ImageTag)" -ForegroundColor Cyan
            Invoke-AcrBuild -Registry $registry -Images @(
                [pscustomobject]@{ Name = "web:$ImageTag"; Dockerfile = 'services/evaluation/Dockerfile.web'; Arguments = @{
                        VITE_ENTRA_TENANT_ID = $TenantId; VITE_ENTRA_CLIENT_ID = $SpaClientId; VITE_ENTRA_API_SCOPE = "api://$ApiAppId/Evaluation.Access" } }
                [pscustomobject]@{ Name = "worker:$ImageTag"; Dockerfile = 'services/evaluation/Dockerfile.worker'; Arguments = @{} }
                [pscustomobject]@{ Name = "judge:$ImageTag"; Dockerfile = 'services/judge-sidecar/Dockerfile'; Arguments = @{} }
            )
        }

        $outputs = Invoke-Deployment -Name 'apex-eval-release' -Overrides @(
            'shouldDeployApps=true', "webImage=$loginServer/web:$ImageTag", "workerImage=$loginServer/worker:$ImageTag",
            "judgeImage=$loginServer/judge:$ImageTag", "clamavImage=$loginServer/$clamavTag")

        $identityName = $outputs.identityName.value
        # Identity names are generated alphanumerics (azid<token>), so bracket quoting is safe.
        if ($identityName -notmatch '^[a-z0-9]+$') { throw "Unexpected identity name '$identityName'." }
        Write-Host "🔐 Granting $identityName database roles" -ForegroundColor Cyan
        Invoke-OperatorSql -Outputs $outputs -Sql @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$identityName')
    CREATE USER [$identityName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$identityName];
ALTER ROLE db_datawriter ADD MEMBER [$identityName];
ALTER ROLE db_ddladmin ADD MEMBER [$identityName];
"@

        Write-Host '🗃️  Running migration job' -ForegroundColor Cyan
        $execution = Invoke-Az containerapp job start --name $outputs.migrationJobName.value --resource-group $ResourceGroupName --query name -o tsv
        Wait-JobExecution -JobName $outputs.migrationJobName.value -ExecutionName $execution

        Write-Host "👤 Bootstrapping platform administrator team '$TeamName'" -ForegroundColor Cyan
        # Flags exactly one platform-admin team: reuses an existing flagged team, or adopts the legacy
        # 'Apex Workshop' team (renaming it), or creates the team. The deployer is its first administrator.
        Invoke-OperatorSql -Outputs $outputs -Parameters @{ '@tenant' = $TenantId; '@name' = $TeamName; '@legacy' = 'Apex Workshop'; '@admin' = $AdminObjectId } -Sql @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @team UNIQUEIDENTIFIER = (SELECT TOP 1 Id FROM Teams WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @tenant AND IsPlatformAdmin = 1);
IF @team IS NULL SET @team = (SELECT TOP 1 Id FROM Teams WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @tenant AND Name IN (@name, @legacy) ORDER BY CASE WHEN Name = @name THEN 0 ELSE 1 END);
IF @team IS NULL
BEGIN
    SET @team = NEWID();
    INSERT INTO Teams (Id, TenantId, Name, PolicyVersion, IsPlatformAdmin, CreatedAt) VALUES (@team, @tenant, @name, 0, 1, SYSDATETIMEOFFSET());
END
ELSE UPDATE Teams SET Name = @name, IsPlatformAdmin = 1 WHERE Id = @team;
IF NOT EXISTS (SELECT 1 FROM Memberships WHERE TeamId = @team AND Subject = @admin)
BEGIN
    INSERT INTO Memberships (TeamId, Subject, Role) VALUES (@team, @admin, 0);
    INSERT INTO Audit (Id, TeamId, Actor, Action, Resource, CreatedAt)
        VALUES (NEWID(), @team, 'operator-bootstrap', 'MembershipBootstrap', @admin, SYSDATETIMEOFFSET());
END
COMMIT;
"@

        Write-Host '🔐 Granting directory invitation permissions to the API identity' -ForegroundColor Cyan
        Grant-DirectoryInvitePermissions -PrincipalId (Invoke-Az identity show --name $identityName --resource-group $ResourceGroupName --query principalId -o tsv)

        $agent = Publish-JudgeAgent -ProjectEndpoint $outputs.foundryProjectEndpoint.value -Image "$loginServer/judge:$ImageTag"
        $settings = Get-Content $Parameters -Raw | ConvertFrom-Json
        $project = Invoke-Az rest --method get --url ("https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/" +
            "$($settings.parameters.foundryResourceGroupName.value)/providers/Microsoft.CognitiveServices/accounts/" +
            "$($settings.parameters.foundryAccountName.value)/projects/$($settings.parameters.foundryProjectName.value)?api-version=2026-09-01") `
            --query properties.agentIdentity.agentIdentityId -o tsv
        # Hosted agents run as their own agent identity; unpublished agents may also use the project's shared identity.
        $principals = @(@($agent.Identities) + @($project) | Where-Object { $_ } | Sort-Object -Unique)
        if ($principals.Count -eq 0) { throw 'No Foundry agent identity found to grant judge model access.' }
        Write-Host "🔐 Granting judge model access to $($principals.Count) agent identit$(if ($principals.Count -eq 1) { 'y' } else { 'ies' })" -ForegroundColor Cyan
        Grant-JudgeModelAccess -PrincipalIds $principals

        & (Join-Path $PSScriptRoot 'Register-EntraApps.ps1') -TenantId $TenantId -RedirectUri $outputs.redirectUri.value `
            -ManagedIdentityPrincipalId (Invoke-Az identity show --name $identityName --resource-group $ResourceGroupName --query principalId -o tsv) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Redirect URI registration failed.' }

        $health = $null
        foreach ($attempt in 1..20) {
            try { $health = Invoke-RestMethod -Uri "$($outputs.webUrl.value)/health" -TimeoutSec 30; break }
            catch { Start-Sleep -Seconds 15 }
        }
        if (-not $health) { throw "Web app did not become healthy at $($outputs.webUrl.value)/health." }

        Write-Host "✅ Deployed: $($outputs.evaluationUrl.value)" -ForegroundColor Green
        [ordered]@{
            evaluationUrl   = $outputs.evaluationUrl.value
            foundryAgent    = "$($agent.Name) v$($agent.Version)"
            agentIdentities = $principals
            imageTag        = $ImageTag
            resourceGroup   = $ResourceGroupName
        } | ConvertTo-Json
        exit 0
    }
    catch {
        Write-Error -ErrorAction Continue "Deploy-Evaluation failed: $($_.Exception.Message)"
        exit 1
    }
}

#endregion Main Execution
