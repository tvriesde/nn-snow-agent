Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $bodyFile = $null
    try {
        $bodyIndex = [Array]::IndexOf($Arguments, '--body')
        if ($bodyIndex -ge 0 -and $bodyIndex + 1 -lt $Arguments.Length -and !$Arguments[$bodyIndex + 1].StartsWith('@')) {
            # az.cmd can strip JSON quotes on Windows; pass request bodies by file.
            $bodyFile = [IO.Path]::GetTempFileName()
            [IO.File]::WriteAllText($bodyFile, $Arguments[$bodyIndex + 1], [Text.UTF8Encoding]::new($false))
            $Arguments = $Arguments.Clone()
            $Arguments[$bodyIndex + 1] = "@$bodyFile"
        }
        $output = & az @Arguments --only-show-errors --output json 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed ($($Arguments[0..([Math]::Min(2,$Arguments.Length-1))] -join ' ')): $output" }
        if ($output) { ($output -join "`n") | ConvertFrom-Json }
    } finally {
        if ($bodyFile) { Remove-Item -LiteralPath $bodyFile -Force }
    }
}

function Assert-AzureContext {
    param([string]$SubscriptionId, [string]$TenantId)
    $account = Invoke-Az @('account', 'show')
    if ($account.id -ne $SubscriptionId -or $account.tenantId -ne $TenantId) {
        throw "Wrong Azure context. Select subscription $SubscriptionId in tenant $TenantId before running this script."
    }
}

function Save-Ownership {
    param($Manifest, [string]$Path)
    $parent = Split-Path $Path -Parent
    if (!(Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $Manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$Path.new" -Encoding utf8
    Move-Item -LiteralPath "$Path.new" -Destination $Path -Force
}

function Invoke-Retry {
    param([scriptblock]$Action, [int]$Attempts = 20, [int]$DelaySeconds = 15)
    for ($i = 1; $i -le $Attempts; $i++) {
        try { return & $Action }
        catch {
            if ($i -eq $Attempts) { throw }
            Write-Verbose "Waiting for RBAC/service readiness ($i/$Attempts)."
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

function Get-DataToken {
    param([string]$Resource)
    (Invoke-Az @('account', 'get-access-token', '--resource', $Resource)).accessToken
}

function Invoke-SearchRequest {
    param([string]$Endpoint, [string]$Method, [string]$Path, [string]$Body)
    $headers = @{ Authorization = "Bearer $(Get-DataToken 'https://search.azure.com')" }
    $parameters = @{ Uri = "$Endpoint/$Path"; Method = $Method; Headers = $headers; ContentType = 'application/json' }
    if ($Body) { $parameters.Body = $Body }
    Invoke-RestMethod @parameters -TimeoutSec 30
}

function Assert-OwnedGroup {
    param($Manifest)
    $group = Invoke-Az @('group', 'show', '--name', $Manifest.resourceGroupName)
    if ($group.id -ine $Manifest.resourceGroupId -or $group.tags.ownershipId -ne $Manifest.ownershipId -or $group.tags.application -ne 'employee-it-helpdesk') {
        throw 'Resource group ownership mismatch; refusing to change or delete it.'
    }
    $foreign = @(Invoke-Az @('resource', 'list', '--resource-group', $Manifest.resourceGroupName) |
        Where-Object { !$_.tags -or !$_.tags.PSObject.Properties['ownershipId'] -or $_.tags.ownershipId -ne $Manifest.ownershipId })
    if ($foreign.Count) { throw 'Resource group contains unowned resources; refusing to change or delete it.' }
}

function Assert-LinuxMcpExecutable {
        param([string]$Path)
        if (!(Test-Path $Path -PathType Leaf)) { throw "Missing pinned MCP executable: $Path" }
        $stream = [IO.File]::OpenRead((Resolve-Path $Path).Path)
        try {
            $bytes = [byte[]]::new(20)
            if ($stream.Read($bytes, 0, $bytes.Length) -ne 20 -or
                $bytes[0] -ne 0x7f -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4c -or $bytes[3] -ne 0x46 -or
                $bytes[4] -ne 2 -or $bytes[5] -ne 1 -or $bytes[18] -ne 0x3e -or $bytes[19] -ne 0) {
                throw 'MCP artifact must be a native Linux x64 ELF executable, not a Windows tool or shell shim.'
            }
        } finally { $stream.Dispose() }
        $expected = 'c6ad14d6f4fd86c0ba4857bcc7418fe0ec7658c8d9948a2f275406e113e18eb4'
        if ((Get-FileHash $Path -Algorithm SHA256).Hash -ine $expected) {
            throw 'MCP binary does not match the verified Azure.Mcp 3.0.0-beta.49 Linux x64 artifact.'
        }
    }

function Restore-PinnedMcpPackage {
    param([string]$Destination, [string]$PackageFeedIndex)
    if (([uri]$PackageFeedIndex).Scheme -ne 'https') { throw 'A trusted HTTPS NuGet service index is required.' }
    $index = Invoke-RestMethod $PackageFeedIndex -TimeoutSec 60
    $addresses = @($index.resources | Where-Object { $_.'@type' -like 'PackageBaseAddress*' })
    if (!$addresses.Count) { throw 'Trusted NuGet service index has no PackageBaseAddress.' }
    $base = ([string]$addresses[0].'@id').TrimEnd('/')
    if (([uri]$base).Scheme -ne 'https') { throw 'PackageBaseAddress must use HTTPS.' }
    $url = "$base/azure.mcp.linux-x64/3.0.0-beta.49/azure.mcp.linux-x64.3.0.0-beta.49.nupkg"
    $archive = Join-Path $Destination 'azure.mcp.linux-x64.3.0.0-beta.49.nupkg'
    $partial = "$archive.download"
    try {
        Invoke-WebRequest $url -OutFile $partial -TimeoutSec 300
        $expectedSha512 = $null
        try { $expectedSha512 = ([string](Invoke-WebRequest "$url.sha512" -TimeoutSec 60).Content).Trim() }
        catch { Write-Verbose 'Feed does not expose nupkg SHA512; native binary is still checked against the fixed verified SHA256.' }
        if ($expectedSha512) {
            $actual = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash $partial -Algorithm SHA512).Hash))
            if ($actual -cne $expectedSha512) { throw 'Trusted-feed package SHA512 mismatch.' }
        }
        Move-Item $partial $archive -Force
    } finally {
        if (Test-Path $partial) { Remove-Item $partial }
    }
    $distribution = Join-Path $Destination 'nuget-distribution'
    if (Test-Path $distribution) { Remove-Item $distribution -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $distribution)
    $specs = @(Get-ChildItem $distribution -File -Filter '*.nuspec')
    if ($specs.Count -ne 1) { throw 'Pinned runtime package must contain exactly one nuspec.' }
    [xml]$spec = Get-Content $specs[0].FullName -Raw
    if ($spec.package.metadata.id -ine 'Azure.Mcp.linux-x64' -or $spec.package.metadata.version -ne '3.0.0-beta.49') {
        throw 'Trusted-feed artifact package identity/version mismatch.'
    }
    $executable = Join-Path $distribution 'tools\any\linux-x64\azmcp'
    Assert-LinuxMcpExecutable $executable
    $executable
}

function Get-PinnedLinuxMcp {
        param(
            [string]$Destination,
            [string]$PackageFeedIndex = 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json'
        )
        $version = '3.0.0-beta.49'
        $hash = '91fd91b6dfd218c6a433fa446730251d6e8fe0cc2b72ea7dc06c79a713b78975'
        $url = "https://github.com/microsoft/mcp/releases/download/Azure.Mcp.Server-$version/Azure.Mcp.Server-linux-x64.zip"
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        $archive = Join-Path $Destination "Azure.Mcp.Server-$version-linux-x64.zip"
        try {
        if (!(Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ine $hash) {
            $partial = "$archive.download"
            try {
                Invoke-WebRequest $url -OutFile $partial -TimeoutSec 300
                if ((Get-FileHash $partial -Algorithm SHA256).Hash -ine $hash) { throw 'Pinned MCP release checksum mismatch; artifact refused.' }
                Move-Item $partial $archive -Force
            } finally {
                if (Test-Path $partial) { Remove-Item $partial }
            }
        }
        $distribution = Join-Path $Destination 'distribution'
        if (Test-Path $distribution) { Remove-Item $distribution -Recurse -Force }
        Expand-Archive $archive -DestinationPath $distribution
        $executables = @(Get-ChildItem $distribution -File -Recurse -Filter 'azmcp')
        if ($executables.Count -ne 1) { throw 'Pinned distribution must contain exactly one azmcp executable.' }
        Assert-LinuxMcpExecutable $executables[0].FullName
        $executables[0].FullName
        } catch {
            Write-Verbose "Official release restore failed; restoring the same pinned Linux version through the configured trusted NuGet service index. $($_.Exception.Message)"
            Restore-PinnedMcpPackage -Destination $Destination -PackageFeedIndex $PackageFeedIndex
        }
    }

function Get-IndexCheckpoint {
    param([string]$BlobServiceUri)
    $response = Invoke-WebRequest -Uri "${BlobServiceUri}indexer-state/checkpoint.json" -TimeoutSec 30 -Headers @{
        Authorization = "Bearer $(Get-DataToken 'https://storage.azure.com/')"
        'x-ms-version' = '2023-11-03'
    }
    $content = if ($response.Content -is [byte[]]) {
        [Text.Encoding]::UTF8.GetString($response.Content)
    } else { $response.Content }
    $checkpoint = $content | ConvertFrom-Json
    @{
        sequence = $checkpoint.sequence
        leaseStatus = $response.Headers['x-ms-lease-status'] -join ''
        leaseState = $response.Headers['x-ms-lease-state'] -join ''
    }
}

function Get-CommittedSourceSequence {
    param([string]$BlobServiceUri)
    $manifest = Invoke-RestMethod -Uri "${BlobServiceUri}servicenow/manifest.json" -TimeoutSec 30 -Headers @{
        Authorization = "Bearer $(Get-DataToken 'https://storage.azure.com/')"
        'x-ms-version' = '2023-11-03'
    }
    if ($manifest.schemaVersion -ne 1 -or !$manifest.batches -or !$manifest.batches.Count) {
        throw 'Missing or unsupported committed source manifest.'
    }
    $sequence = 0
    foreach ($batch in $manifest.batches) {
        $sequence++
        if ($batch.sequence -ne $sequence -or $batch.kind -notin @('snapshot', 'delta') -or
            $batch.path -notmatch '^(baseline|changes)/[a-zA-Z0-9_-]+\.json$' -or $batch.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
            throw 'Committed source manifest descriptors are invalid or noncontiguous.'
        }
    }
    $sequence
}

function Get-IngestionRequirements {
    param([bool]$InitialSeed, [int]$ExpectedDocumentCount = 0, [int]$ExpectedCheckpointSequence = 0)
    @{
        documentCount = if ($InitialSeed -and $ExpectedDocumentCount -eq 0) { 176 } else { $ExpectedDocumentCount }
        checkpointSequence = if ($InitialSeed -and $ExpectedCheckpointSequence -eq 0) { 5 } else { $ExpectedCheckpointSequence }
    }
}

function Wait-SearchIngestion {
        param(
            [string]$Endpoint,
            [int]$ExpectedDocumentCount = 0,
            [long]$MaximumStorageBytes = 26214400,
            [int]$TimeoutSeconds = 900,
            [int]$PollSeconds = 15,
            [string]$BlobServiceUri,
            [int]$ExpectedCheckpointSequence = 0
        )
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        $last = 'No index statistics response.'
        do {
            try {
                $stats = Invoke-SearchRequest $Endpoint GET 'indexes/servicenow-knowledge/stats?api-version=2024-07-01'
                $last = "documentCount=$($stats.documentCount), storageSize=$($stats.storageSize)"
                if ($stats.storageSize -gt $MaximumStorageBytes) {
                    throw "Index exceeds the demo storage budget ($last; maximum=$MaximumStorageBytes bytes)."
                }
                $checkpointReady = $true
                if ($BlobServiceUri) {
                    $committedSequence = Get-CommittedSourceSequence $BlobServiceUri
                    $checkpoint = Get-IndexCheckpoint $BlobServiceUri
                    $last += ", committedSequence=$committedSequence, checkpoint=$($checkpoint.sequence), lease=$($checkpoint.leaseStatus)/$($checkpoint.leaseState)"
                    $checkpointReady = $checkpoint.sequence -eq $committedSequence -and
                        ($ExpectedCheckpointSequence -eq 0 -or $committedSequence -eq $ExpectedCheckpointSequence) -and
                        $checkpoint.leaseStatus -eq 'unlocked' -and $checkpoint.leaseState -eq 'available'
                }
                if (($ExpectedDocumentCount -eq 0 -or $stats.documentCount -eq $ExpectedDocumentCount) -and $checkpointReady) {
                    Write-Output "Verified indexed knowledge: $last."
                    return
                }
            } catch {
                if ($_.Exception.Message -like 'Index exceeds*') { throw }
                $last = $_.Exception.Message
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
            Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalSeconds)))
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        $countRequirement = if ($ExpectedDocumentCount) { "$ExpectedDocumentCount documents" } else { 'current measured document count (no initial-baseline count assumption)' }
        throw "Scheduled ingestion verification timed out after $TimeoutSeconds seconds; expected $countRequirement, <=$MaximumStorageBytes bytes and checkpoint at the latest committed manifest sequence with released lease. Last observation: $last. Deployment is NOT verified; inspect Function logs, permissions, manifest and checkpoint. No source or state was reset."
    }
