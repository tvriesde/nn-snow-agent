#requires -Version 7.2
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$SubscriptionId = 'd860292c-5d2c-4df3-b7c8-332bd46882d1',
    [string]$TenantId = '47c94d43-bd0b-4cc0-9c81-412496225c31',
    [string]$Location = 'swedencentral',
    [ValidatePattern('^[a-z][a-z0-9-]{2,17}$')][string]$NamePrefix = 'snowdemo',
    [string]$ResourceGroupName = "$NamePrefix-rg",
    [string]$BackendClientId,
    [string]$FrontendClientId,
    [Alias('CreateAppRegistrations')][switch]$CreateEntraApplications,
    [string]$ModelVersion,
    [ValidateSet('Standard', 'DataZoneStandard')][string]$ModelSku,
    [string]$ModelLocation = $Location,
    [string]$ModelName = 'gpt-5-nano',
    [ValidateRange(1, 100)][int]$ModelCapacity = 50,
    [string]$OperatorObjectId,
    [ValidateSet('User', 'ServicePrincipal')][string]$OperatorPrincipalType = 'User',
    [string]$BackendProject = 'src\backend\Helpdesk.Backend.csproj',
    [string]$IndexerProject = 'src\indexer\Helpdesk.Indexer.csproj',
    [string]$FrontendDirectory = 'src\frontend',
    [string]$McpExecutablePath,
    [switch]$DownloadPinnedMcp,
    [string]$McpPackageFeedIndex = 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json',
    [string]$HealthAccessToken,
    [scriptblock]$HealthTokenProvider,
    [string]$ProtectedApiPath = '/api/examples',
    [switch]$SeedDemoData,
    [switch]$ResumePartialSeed,
    [ValidateRange(0, 100000)][int]$ExpectedDocumentCount = 0,
    [ValidateRange(0, 100000)][int]$ExpectedCheckpointSequence = 0,
    [ValidateRange(1, 3600)][int]$IngestionTimeoutSeconds = 1200,
    [string]$OwnershipManifest = '.azure\ownership.json'
)
. "$PSScriptRoot\common.ps1"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # WhatIf exits before CLI, Graph, builds, token acquisition or any file write.
    if (!$PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroupName", 'Create/update owned helpdesk resources, register opted-in Entra apps, publish packages and optionally seed empty source')) { return }
    if (!$ModelVersion -or !$ModelSku) { throw 'Specify a verified ModelVersion and ModelSku (Standard or DataZoneStandard). No Global or paid-SKU fallback is permitted.' }
    if ($ResumePartialSeed -and !$SeedDemoData) { throw 'ResumePartialSeed requires SeedDemoData.' }
    if ($CreateEntraApplications -and ($BackendClientId -or $FrontendClientId)) { throw 'Use existing IDs OR CreateEntraApplications, not both.' }
    if (!$CreateEntraApplications -and (!$BackendClientId -or !$FrontendClientId) -and !(Test-Path $OwnershipManifest)) {
        throw 'Supply both existing Entra client IDs, or explicitly opt into CreateEntraApplications.'
    }
    foreach ($path in @($BackendProject, $IndexerProject, "$FrontendDirectory\package.json", 'src\core\search-index.json')) {
        if (!(Test-Path $path)) { throw "Required application artifact is missing: $path" }
    }
    if ($DownloadPinnedMcp -and $McpExecutablePath) { throw 'Choose DownloadPinnedMcp OR an existing McpExecutablePath, not both.' }
    if (!$McpExecutablePath) { $McpExecutablePath = Get-PinnedLinuxMcp -Destination (Join-Path $root '.azure\mcp\3.0.0-beta.48') -PackageFeedIndex $McpPackageFeedIndex }
    Assert-LinuxMcpExecutable $McpExecutablePath
    Assert-AzureContext $SubscriptionId $TenantId
    if (Test-Path $OwnershipManifest) {
        $manifest = Get-Content $OwnershipManifest -Raw | ConvertFrom-Json -AsHashtable
        if ($manifest.subscriptionId -ne $SubscriptionId -or $manifest.tenantId -ne $TenantId -or $manifest.resourceGroupName -ne $ResourceGroupName) { throw 'Ownership manifest context mismatch.' }
        $BackendClientId = $manifest.backendClientId
        $FrontendClientId = $manifest.frontendClientId
        if ($manifest.resourceGroupId) { Assert-OwnedGroup $manifest }
    } else {
        if (Invoke-Az @('group', 'exists', '--name', $ResourceGroupName)) { throw 'An existing resource group has no ownership manifest; refusing adoption.' }
        $manifest = @{
            version = 1; ownershipId = [guid]::NewGuid().ToString(); subscriptionId = $SubscriptionId; tenantId = $TenantId
            resourceGroupName = $ResourceGroupName; resourceGroupId = $null; readerRoleAssignmentId = $null
            backendClientId = $BackendClientId; frontendClientId = $FrontendClientId
            ownedApplications = @(); temporaryRoleAssignmentIds = @(); resources = $null
        }
        Save-Ownership $manifest $OwnershipManifest
    }
    if (!$OperatorObjectId) {
        if ($OperatorPrincipalType -ne 'User') { throw 'Service principal deployments must supply OperatorObjectId.' }
        $OperatorObjectId = (Invoke-Az @('ad', 'signed-in-user', 'show')).id
    }
    $manifest.mcpArtifact = @{
        packageId = 'Azure.Mcp.linux-x64'
        version = '3.0.0-beta.48'
        runtime = 'linux-x64'
        executableSha256 = (Get-FileHash $McpExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($manifest.ContainsKey('operatorObjectId') -and $manifest.operatorObjectId -ne $OperatorObjectId -and $manifest.temporaryRoleAssignmentIds.Count) {
        throw 'Uncleaned temporary grants belong to a different operator. Clean them with the original operator or delete the owned deployment first.'
    }
    $manifest.operatorObjectId = $OperatorObjectId
    Save-Ownership $manifest $OwnershipManifest
    $searchServices = @()
    $searchPageUrl = "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Search/searchServices?api-version=2023-11-01"
    while ($searchPageUrl) {
        $searchPage = Invoke-Az @('rest', '--method', 'GET', '--url', $searchPageUrl)
        $searchServices += @($searchPage.value)
        $searchPageUrl = if ($searchPage.PSObject.Properties['nextLink']) { $searchPage.nextLink } else { $null }
    }
    $freeSearch = @($searchServices | Where-Object {
        $_.sku.name -eq 'free' -and $_.id -notlike "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/*"
    })
    if ($freeSearch.Count) { throw 'An unrelated Free Search service already occupies the subscription limit. It will not be changed or replaced.' }
    $runtimes = @(Invoke-Az @('functionapp', 'list-flexconsumption-runtimes', '--location', $Location, '--runtime', 'dotnet-isolated'))
    if (!@($runtimes | Where-Object {
        $_.version -eq '10' -and $_.sku.functionAppConfigProperties.runtime.version -eq '10.0'
    }).Count) { throw 'Flex .NET 10 with ARM runtime version 10.0 is unavailable in the selected region. No hosting/runtime fallback.' }
    $webRuntimes = @(Invoke-Az @('webapp', 'list-runtimes', '--os', 'linux'))
    if ('DOTNETCORE:10.0' -notin $webRuntimes -or 'NODE:24-lts' -notin $webRuntimes) {
        throw 'Required Linux App Service .NET 10/Node 24 runtime is unavailable. No runtime or paid-plan fallback.'
    }

    # Graph registrations are explicitly separate from ARM resources.
    if (!$manifest.backendClientId -and $CreateEntraApplications) {
        $app = Invoke-Az @('ad', 'app', 'create', '--display-name', "Employee IT Helpdesk API [$($manifest.ownershipId)]", '--sign-in-audience', 'AzureADMyOrg')
        $manifest.backendClientId = $app.appId
        $manifest.ownedApplications += @{ objectId = $app.id; clientId = $app.appId; displayName = $app.displayName }
        Save-Ownership $manifest $OwnershipManifest
    }
    if (!$manifest.frontendClientId -and $CreateEntraApplications) {
        $app = Invoke-Az @('ad', 'app', 'create', '--display-name', "Employee IT Helpdesk SPA [$($manifest.ownershipId)]", '--sign-in-audience', 'AzureADMyOrg')
        $manifest.frontendClientId = $app.appId
        $manifest.ownedApplications += @{ objectId = $app.id; clientId = $app.appId; displayName = $app.displayName }
        Save-Ownership $manifest $OwnershipManifest
    }
    $BackendClientId = $manifest.backendClientId
    $FrontendClientId = $manifest.frontendClientId
    $backendRegistration = Invoke-Az @('ad', 'app', 'show', '--id', $BackendClientId)
    $frontendRegistration = Invoke-Az @('ad', 'app', 'show', '--id', $FrontendClientId)
    if ($backendRegistration.signInAudience -ne 'AzureADMyOrg' -or $frontendRegistration.signInAudience -ne 'AzureADMyOrg') { throw 'Both registrations must be single-tenant AzureADMyOrg.' }
    $ownedBackend = @($manifest.ownedApplications | Where-Object { $_.clientId -eq $BackendClientId }).Count -gt 0
    foreach ($ownedApp in @($manifest.ownedApplications)) {
        $actual = Invoke-Az @('ad', 'app', 'show', '--id', $ownedApp.clientId)
        if ($actual.id -ne $ownedApp.objectId -or $actual.displayName -ne $ownedApp.displayName -or !$actual.displayName.Contains("[$($manifest.ownershipId)]")) {
            throw 'Owned Entra registration metadata mismatch; refusing modification.'
        }
        $principals = @(Invoke-Az @('ad', 'sp', 'list', '--filter', "appId eq '$($ownedApp.clientId)'"))
        if ($principals.Count -gt 1) { throw 'Ambiguous owned enterprise application lookup.' }
        if (!$principals.Count) {
            $principal = Invoke-Az @('ad', 'sp', 'create', '--id', $ownedApp.clientId)
            $ownedApp.servicePrincipalId = $principal.id
        } else {
            $ownedApp.servicePrincipalId = $principals[0].id
        }
        Save-Ownership $manifest $OwnershipManifest
    }
    if ($ownedBackend) {
        $scopeId = [guid]::NewGuid().ToString()
        if ($backendRegistration.api.oauth2PermissionScopes.Count) { $scopeId = $backendRegistration.api.oauth2PermissionScopes[0].id }
        $body = @{
            identifierUris = @("api://$BackendClientId")
            api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = @(@{
                id = $scopeId; value = 'Helpdesk.Access'; type = 'User'; isEnabled = $true
                adminConsentDisplayName = 'Access employee IT helpdesk'; adminConsentDescription = 'Ask the employee IT helpdesk for assistance.'
                userConsentDisplayName = 'Access employee IT helpdesk'; userConsentDescription = 'Ask the employee IT helpdesk for assistance.'
            }) }
        } | ConvertTo-Json -Depth 12 -Compress
        Invoke-Az @('rest', '--method', 'PATCH', '--url', "https://graph.microsoft.com/v1.0/applications/$($backendRegistration.id)", '--headers', 'Content-Type=application/json', '--body', $body) | Out-Null
        $preauthorization = @{ api = @{ preAuthorizedApplications = @(@{
            appId = $FrontendClientId; delegatedPermissionIds = @("$scopeId")
        }) } } | ConvertTo-Json -Depth 8 -Compress
        Invoke-Retry {
            Invoke-Az @('rest', '--method', 'PATCH', '--url', "https://graph.microsoft.com/v1.0/applications/$($backendRegistration.id)", '--headers', 'Content-Type=application/json', '--body', $preauthorization) | Out-Null
        }
    } else {
        $scopes = @($backendRegistration.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'Helpdesk.Access' -and $_.isEnabled })
        if ($scopes.Count -ne 1 -or $backendRegistration.api.requestedAccessTokenVersion -ne 2 -or "api://$BackendClientId" -notin $backendRegistration.identifierUris) {
            throw 'Existing backend registration must expose api://<clientId>/Helpdesk.Access with v2 tokens. Existing registrations are never modified.'
        }
        $scopeId = $scopes[0].id
    }

    $models = Invoke-Az @('cognitiveservices', 'model', 'list', '--location', $ModelLocation)
    $candidate = @($models | Where-Object { $_.model.name -eq $ModelName -and $_.model.version -eq $ModelVersion -and @($_.model.skus.name) -contains $ModelSku })
    if (!$candidate.Count) { throw "Model $ModelName/$ModelVersion with $ModelSku is unavailable in $ModelLocation. Choose explicitly after checking residency and quota; no silent fallback." }
    $usage = @(Invoke-Az @('cognitiveservices', 'usage', 'list', '--location', $ModelLocation) |
        Where-Object { $_.name.value -eq "OpenAI.$ModelSku.$ModelName" })
    if (!$usage.Count -or ($usage[0].limit - $usage[0].currentValue) -lt $ModelCapacity) { throw 'Selected model quota is unavailable or cannot be verified. No SKU/region fallback.' }
    $deploymentName = "$NamePrefix-$($manifest.ownershipId.Substring(0,8))"
    $parameters = @{
        location = $Location; namePrefix = $NamePrefix; resourceGroupName = $ResourceGroupName; ownershipId = $manifest.ownershipId
        backendClientId = $BackendClientId; frontendClientId = $FrontendClientId; tenantId = $TenantId
        modelName = $ModelName; modelVersion = $ModelVersion; modelSku = $ModelSku; modelCapacity = $ModelCapacity; modelLocation = $ModelLocation
    }
    $parameterFile = Join-Path $root '.azure\deployment.parameters.json'
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'
        parameters = $parameters.GetEnumerator() | ForEach-Object -Begin { $p = @{} } -Process { $p[$_.Key] = @{ value = $_.Value } } -End { $p }
    } | ConvertTo-Json -Depth 10 | Set-Content $parameterFile
    # Persist deterministic outside-RG cleanup coordinates before ARM execution.
    $manifest.resourceGroupId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
    Save-Ownership $manifest $OwnershipManifest
    $deployment = Invoke-Az @('deployment', 'sub', 'create', '--name', $deploymentName, '--location', $Location, '--template-file', 'infra\main.bicep', '--parameters', "@$parameterFile")
    $r = $deployment.properties.outputs.resources.value
    $manifest.resources = $r
    $manifest.readerRoleAssignmentId = $deployment.properties.outputs.readerRoleAssignmentId.value
    Save-Ownership $manifest $OwnershipManifest

    $ownedFrontend = @($manifest.ownedApplications | Where-Object { $_.clientId -eq $FrontendClientId }).Count -gt 0
    if ($ownedFrontend) {
        $body = @{ spa = @{ redirectUris = @("$($r.frontendUrl)/") }; requiredResourceAccess = @(@{
            resourceAppId = $BackendClientId; resourceAccess = @(@{ id = $scopeId; type = 'Scope' })
        }) } | ConvertTo-Json -Depth 8 -Compress
        Invoke-Az @('rest', '--method', 'PATCH', '--url', "https://graph.microsoft.com/v1.0/applications/$($frontendRegistration.id)", '--headers', 'Content-Type=application/json', '--body', $body) | Out-Null
    } elseif (!@($frontendRegistration.spa.redirectUris | Where-Object { $_.TrimEnd('/') -eq $r.frontendUrl }).Count) {
        throw "Existing SPA registration needs redirect URI $($r.frontendUrl)/. Configure it explicitly and rerun; no existing registration was modified."
    }

    # Temporary operator privileges are scoped and journaled, including on failure.
    try {
        foreach ($grant in @(
            @{ scope = $r.storageId; role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
            @{ scope = $r.searchId; role = '7ca78c08-252a-4471-8644-bb5ff32d4ba0' }
            @{ scope = $r.vaultId; role = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' }
        )) {
            $existing = @(Invoke-Az @('role', 'assignment', 'list', '--scope', $grant.scope, '--assignee', $OperatorObjectId) |
                Where-Object { $_.scope -ieq $grant.scope -and $_.roleDefinitionId -like "*/$($grant.role)" })
            if (!$existing.Count) {
                $roleName = [guid]::NewGuid().ToString()
                $roleId = "$($grant.scope)/providers/Microsoft.Authorization/roleAssignments/$roleName"
                $manifest.temporaryRoleAssignmentIds += $roleId
                Save-Ownership $manifest $OwnershipManifest
                Invoke-Az @('role', 'assignment', 'create', '--name', $roleName, '--assignee-object-id', $OperatorObjectId, '--assignee-principal-type', $OperatorPrincipalType, '--role', $grant.role, '--scope', $grant.scope) | Out-Null
            }
        }
        $keys = Invoke-Az @('cognitiveservices', 'account', 'keys', 'list', '--resource-group', $ResourceGroupName, '--name', ($r.openAIId -split '/')[-1])
        $secretBody = @{ value = $keys.key1 } | ConvertTo-Json -Compress
        try {
            Invoke-Retry {
                Invoke-RestMethod -Method Put -Uri "https://$($r.vaultName).vault.azure.net/secrets/azure-openai-key?api-version=7.4" -Headers @{
                    Authorization = "Bearer $(Get-DataToken 'https://vault.azure.net')"
                } -ContentType 'application/json' -Body $secretBody | Out-Null
            }
        } finally {
            $keys = $null
            $secretBody = $null
        }
        Invoke-Az @('rest', '--method', 'POST', '--url', "https://management.azure.com$($r.backendId)/config/configreferences/appsettings/refresh?api-version=2022-03-01") | Out-Null
        $schema = Get-Content 'src\core\search-index.json' -Raw
        Invoke-Retry { Invoke-SearchRequest $r.searchEndpoint PUT 'indexes/servicenow-knowledge?api-version=2024-07-01' $schema } | Out-Null
        if ($SeedDemoData) {
            $blobs = @(Invoke-Retry { Invoke-Az @('storage', 'blob', 'list', '--account-name', $r.storageName, '--container-name', 'servicenow', '--auth-mode', 'login') })
            $state = @(Invoke-Retry { Invoke-Az @('storage', 'blob', 'list', '--account-name', $r.storageName, '--container-name', 'indexer-state', '--auth-mode', 'login') })
            if ($state.Count -or @($blobs | Where-Object { $_.name -eq 'manifest.json' }).Count) {
                throw 'Seed refused: committed source or checkpoint already exists. Publish a delta instead; baseline/checkpoints are never reset.'
            }
            if ($blobs.Count -and !$ResumePartialSeed) { throw 'Partial seed exists. Explicitly approve ResumePartialSeed to verify existing bytes and upload only missing files.' }
            if (!(Test-Path 'data\servicenow\manifest.json')) { throw 'Generate demo fixtures first.' }
            $seedFiles = @(Get-ChildItem 'data\servicenow' -File -Recurse | Where-Object { $_.FullName -ne (Join-Path $root 'data\servicenow\manifest.json') })
            $seedNames = @($seedFiles | ForEach-Object { [IO.Path]::GetRelativePath((Join-Path $root 'data\servicenow'), $_.FullName).Replace('\','/') })
            $existingSeedNames = @($blobs | ForEach-Object { $_.name })
            foreach ($blob in $blobs) {
                if ($blob.name -cnotin $seedNames) { throw "Partial seed contains an unexpected blob: $($blob.name)" }
                $localPath = Join-Path (Join-Path $root 'data\servicenow') $blob.name.Replace('/', '\')
                $response = Invoke-Retry {
                    Invoke-WebRequest -Uri "$($r.storageBlobEndpoint.TrimEnd('/'))/servicenow/$($blob.name)" -Headers @{
                        Authorization = "Bearer $(Get-DataToken 'https://storage.azure.com/')"
                        'x-ms-version' = '2023-11-03'
                    } -TimeoutSec 60
                }
                $remoteHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($response.RawContentStream.ToArray()))
                if ($remoteHash -ne (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash) {
                    throw "Partial seed bytes differ from the approved fixture: $($blob.name). No data was overwritten."
                }
            }
            foreach ($file in $seedFiles) {
                $blobName = [IO.Path]::GetRelativePath((Join-Path $root 'data\servicenow'), $file.FullName).Replace('\','/')
                if ($blobName -cin $existingSeedNames) { continue }
                Invoke-Retry { Invoke-Az @('storage', 'blob', 'upload', '--account-name', $r.storageName, '--container-name', 'servicenow', '--auth-mode', 'login', '--file', $file.FullName, '--name', $blobName, '--overwrite', 'false', '--no-progress') } | Out-Null
            }
            Invoke-Retry { Invoke-Az @('storage', 'blob', 'upload', '--account-name', $r.storageName, '--container-name', 'servicenow', '--auth-mode', 'login', '--file', 'data\servicenow\manifest.json', '--name', 'manifest.json', '--overwrite', 'false', '--no-progress') } | Out-Null
        }
        $build = Join-Path $root '.azure\packages'
        New-Item -ItemType Directory -Force $build | Out-Null
        foreach ($app in @(@{ project = $BackendProject; folder = 'backend' }, @{ project = $IndexerProject; folder = 'indexer' })) {
            $dest = Join-Path $build $app.folder
            if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
            & dotnet publish $app.project -c Release -o $dest --nologo
            if ($LASTEXITCODE) { throw "dotnet publish failed: $($app.project)" }
        }
        $mcpTarget = Join-Path $build 'backend\mcp'
        New-Item -ItemType Directory -Force $mcpTarget | Out-Null
        Copy-Item "$(Split-Path $McpExecutablePath -Parent)\*" $mcpTarget -Recurse -Force
        if ((Split-Path $McpExecutablePath -Leaf) -ne 'azmcp') { Copy-Item $McpExecutablePath (Join-Path $mcpTarget 'azmcp') -Force }
        # Preserve executable metadata; the Linux startup command also chmods azmcp.
        Add-Type -AssemblyName System.IO.Compression
        foreach ($app in @('backend', 'indexer')) {
            $zipPath = Join-Path $build "$app.zip"
            if (Test-Path $zipPath) { Remove-Item $zipPath }
            $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
            try {
                foreach ($file in Get-ChildItem (Join-Path $build $app) -File -Recurse) {
                    $entryName = [IO.Path]::GetRelativePath((Join-Path $build $app), $file.FullName).Replace('\','/')
                    $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName)
                    if ($entryName -eq 'mcp/azmcp') { $entry.ExternalAttributes = (33261 -shl 16) }
                }
            } finally { $zip.Dispose() }
        }
        Push-Location $FrontendDirectory
        try {
            & npm ls --depth=0 --silent *> $null
            if ($LASTEXITCODE) {
                & npm ci --no-audit --no-fund
                if ($LASTEXITCODE) { throw 'npm ci failed.' }
            }
            & npm run typecheck
            if ($LASTEXITCODE) { throw 'Frontend typecheck failed.' }
            & npm run build
            if ($LASTEXITCODE) { throw 'Frontend build failed.' }
        } finally { Pop-Location }
        $frontendPackage = Join-Path $build 'frontend.zip'
        if (Test-Path $frontendPackage) { Remove-Item $frontendPackage }
        # The production server uses Node built-ins; do not ship Expo build dependencies.
        Compress-Archive -Path "$FrontendDirectory\dist", "$FrontendDirectory\server.mjs", "$FrontendDirectory\package.json" -DestinationPath $frontendPackage
        Invoke-Retry { Invoke-Az @('webapp', 'deploy', '--resource-group', $ResourceGroupName, '--name', $r.backendName, '--src-path', (Join-Path $build 'backend.zip'), '--type', 'zip', '--clean', 'true') } | Out-Null
        Invoke-Retry { Invoke-Az @('webapp', 'deploy', '--resource-group', $ResourceGroupName, '--name', $r.frontendName, '--src-path', $frontendPackage, '--type', 'zip', '--clean', 'true') } | Out-Null
        Invoke-Retry { Invoke-Az @('functionapp', 'deployment', 'source', 'config-zip', '--resource-group', $ResourceGroupName, '--name', $r.indexerName, '--src', (Join-Path $build 'indexer.zip')) } | Out-Null
        Invoke-Retry { Invoke-WebRequest "$($r.backendUrl)/health/live" -TimeoutSec 60 | Out-Null }
        Invoke-Retry { Invoke-WebRequest $r.frontendUrl -TimeoutSec 60 | Out-Null }
        Invoke-Retry {
            $functionKeys = Invoke-Az @('functionapp', 'keys', 'list', '--resource-group', $ResourceGroupName, '--name', $r.indexerName)
            $status = Invoke-RestMethod "$($r.indexerUrl)/admin/host/status" -Headers @{ 'x-functions-key' = $functionKeys.masterKey } -TimeoutSec 60
            if ($status.state -ne 'Running') { throw 'Function host has not reached Running state.' }
            $functionKeys = $null
        }
        Invoke-Retry {
            $functions = @(Invoke-Az @('functionapp', 'function', 'list', '--resource-group', $ResourceGroupName, '--name', $r.indexerName))
            $names = @($functions | ForEach-Object { ($_.name -split '/')[-1] })
            if ('IndexServiceNowKnowledge' -notin $names -or 'ProbeHelpdeskAvailability' -notin $names) {
                throw 'Published Function metadata is missing IndexServiceNowKnowledge or ProbeHelpdeskAvailability.'
            }
        }
        $requirements = Get-IngestionRequirements -InitialSeed ([bool]$SeedDemoData) -ExpectedDocumentCount $ExpectedDocumentCount -ExpectedCheckpointSequence $ExpectedCheckpointSequence
        Wait-SearchIngestion -Endpoint $r.searchEndpoint -ExpectedDocumentCount $requirements.documentCount -TimeoutSeconds $IngestionTimeoutSeconds -BlobServiceUri $r.storageBlobEndpoint -ExpectedCheckpointSequence $requirements.checkpointSequence
        $unauthorized = Invoke-WebRequest "$($r.backendUrl)$ProtectedApiPath" -SkipHttpErrorCheck
        if ($unauthorized.StatusCode -ne 401) { throw "Protected API did not reject anonymous traffic: $($unauthorized.StatusCode)" }
        if ($HealthTokenProvider) { $HealthAccessToken = & $HealthTokenProvider $BackendClientId $FrontendClientId $TenantId $r.frontendUrl }
        if (!$HealthAccessToken) { throw "Published, but authenticated verification is incomplete. Sign in to the SPA at $($r.frontendUrl) and rerun with HealthAccessToken (delegated api://$BackendClientId/Helpdesk.Access), or supply HealthTokenProvider to obtain it after registrations exist." }
        $authorized = Invoke-WebRequest "$($r.backendUrl)$ProtectedApiPath" -Headers @{ Authorization = "Bearer $HealthAccessToken" } -SkipHttpErrorCheck -TimeoutSec 120
        if ($authorized.StatusCode -ne 200) { throw "Authenticated API check failed: $($authorized.StatusCode)" }
        Write-Output "Published owned demo. Frontend: $($r.frontendUrl). Ownership: $OwnershipManifest"
        Write-Output 'Verified hosting, scoped API authentication and index statistics only. GPT responses and live Azure MCP investigations still require an explicit employee smoke test; this script does not claim those integrations succeeded.'
    } finally {
        $cleanupFailures = @()
        foreach ($id in @($manifest.temporaryRoleAssignmentIds)) {
            try {
                Invoke-Az @('role', 'assignment', 'delete', '--ids', $id) | Out-Null
                $manifest.temporaryRoleAssignmentIds = @($manifest.temporaryRoleAssignmentIds | Where-Object { $_ -ne $id })
                Save-Ownership $manifest $OwnershipManifest
            } catch {
                $cleanupFailures += $id
                Write-Warning "Temporary grant could not be removed; retained in ownership manifest: $id"
            }
        }
        if ($cleanupFailures.Count) { throw 'Temporary operator permission cleanup incomplete. Rerun with the same operator or use delete.ps1; ownership manifest retains exact IDs.' }
    }
} finally { Pop-Location }
