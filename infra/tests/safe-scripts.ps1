#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$paths = @('scripts\common.ps1', 'scripts\deploy.ps1', 'scripts\delete.ps1')
foreach ($path in $paths) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $path), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Parse failure in ${path}: $errors" }
    if ($path -eq 'scripts\deploy.ps1') {
        $grantLoops = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.ForEachStatementAst] -and
                $node.Variable.VariablePath.UserPath -eq 'grant'
        }, $true))
        if ($grantLoops.Count -ne 1 -or $grantLoops[0].Body.Extent.Text -match 'vault.azure.net/secrets') {
            throw 'Model secret initialization must run once after all temporary grants are assigned.'
        }
    }
}
$script:azureCalls = 0
function global:az {
    $script:azureCalls++
    throw 'WhatIf must not invoke Azure CLI.'
}
$testManifest = Join-Path $PSScriptRoot 'whatif-ownership.json'
if (Test-Path $testManifest) { throw 'Refusing to overwrite an existing test file.' }
try {
    @{
        version = 1
        ownershipId = '00000000-0000-0000-0000-000000000001'
        subscriptionId = 'd860292c-5d2c-4df3-b7c8-332bd46882d1'
        tenantId = '47c94d43-bd0b-4cc0-9c81-412496225c31'
        resourceGroupName = 'whatif-test-never-create'
    } | ConvertTo-Json | Set-Content $testManifest
    & (Join-Path $root 'scripts\deploy.ps1') -WhatIf
    & (Join-Path $root 'scripts\deploy.ps1') -DownloadPinnedMcp -CreateAppRegistrations -SeedDemoData -WhatIf
    & (Join-Path $root 'scripts\delete.ps1') -OwnershipManifest $testManifest -DeleteOwnedEntraApplications -WhatIf
    if ($script:azureCalls -ne 0) { throw 'WhatIf contacted Azure.' }
    . (Join-Path $root 'scripts\common.ps1')
    foreach ($helper in @('Get-PinnedLinuxMcp', 'Restore-PinnedMcpPackage', 'Assert-LinuxMcpExecutable', 'Get-IndexCheckpoint', 'Get-CommittedSourceSequence', 'Get-IngestionRequirements', 'Wait-SearchIngestion')) {
        if (!(Get-Command $helper -CommandType Function -ErrorAction SilentlyContinue)) { throw "Missing public helper: $helper" }
    }
    $script:lastArguments = @()
    function global:az {
        $script:lastArguments = @($args)
        $global:LASTEXITCODE = 0
        '{"id":"test-subscription","tenantId":"test-tenant"}'
    }
    $account = Invoke-Az @('account', 'show')
    if ($account.id -ne 'test-subscription' -or ($script:lastArguments -join ' ') -ne 'account show --only-show-errors --output json') {
        throw 'CLI forwarding/JSON parsing regression.'
    }
    $script:requestFile = $null
    function global:az {
        $bodyIndex = [Array]::IndexOf(@($args), '--body')
        $script:requestFile = $args[$bodyIndex + 1].Substring(1)
        $body = Get-Content -LiteralPath $script:requestFile -Raw | ConvertFrom-Json
        if ($body.example -ne 'quoted "value"') { throw 'JSON request body lost quotes.' }
        $global:LASTEXITCODE = 0
        '{"id":"test-subscription","tenantId":"test-tenant"}'
    }
    Invoke-Az @('rest', '--method', 'PATCH', '--body', '{"example":"quoted \"value\""}') | Out-Null
    if (!$script:requestFile -or (Test-Path -LiteralPath $script:requestFile)) { throw 'CLI request body temporary file was not cleaned up.' }
    function global:az {
        $global:LASTEXITCODE = 0
        '{"id":"test-subscription","tenantId":"test-tenant","accessToken":"test-only"}'
    }
    $rejected = $false
    try { Assert-AzureContext 'different-subscription' 'test-tenant' } catch { $rejected = $true }
    if (!$rejected) { throw 'Wrong subscription was not rejected.' }
    $script:attempt = 0
    $result = Invoke-Retry -Attempts 2 -DelaySeconds 0 -Action {
        $script:attempt++
        if ($script:attempt -eq 1) { throw 'Simulated propagation delay.' }
        'ready'
    }
    if ($result -ne 'ready' -or $script:attempt -ne 2) { throw 'Bounded retry regression.' }
    function Invoke-WebRequest {
        param($Uri, $TimeoutSec, $Headers)
        @{
            Content = $script:checkpointContent
            Headers = @{ 'x-ms-lease-status' = 'unlocked'; 'x-ms-lease-state' = 'available' }
        }
    }
    foreach ($binary in @($false, $true)) {
        $script:checkpointContent = if ($binary) { ,([Text.Encoding]::UTF8.GetBytes('{"sequence":5}')) } else { '{"sequence":5}' }
        $checkpoint = Get-IndexCheckpoint 'https://not-contacted.invalid/'
        if ($checkpoint.sequence -ne 5 -or $checkpoint.leaseStatus -ne 'unlocked' -or $checkpoint.leaseState -ne 'available') {
            throw 'Text/binary checkpoint response decoding regression.'
        }
    }
    function Invoke-SearchRequest { @{ documentCount = 176; storageSize = 1024 } }
    Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -ExpectedDocumentCount 176 -TimeoutSeconds 0 | Out-Null
    function Invoke-SearchRequest { @{ documentCount = 175; storageSize = 1024 } }
    $rejected = $false
    try { Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -ExpectedDocumentCount 176 -TimeoutSeconds 0 | Out-Null } catch { $rejected = $_.Exception.Message -like '*NOT verified*' }
    if (!$rejected) { throw 'Incomplete ingestion must not pass.' }
    function Invoke-SearchRequest { @{ documentCount = 176; storageSize = 26214401 } }
    $rejected = $false
    try { Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -TimeoutSeconds 0 | Out-Null } catch { $rejected = $_.Exception.Message -like 'Index exceeds*' }
    if (!$rejected) { throw 'Oversized index must not pass.' }
    function Invoke-SearchRequest { @{ documentCount = 176; storageSize = 1024 } }
    function Get-CommittedSourceSequence { 5 }
    function Get-IndexCheckpoint { @{ sequence = 5; leaseStatus = 'unlocked'; leaseState = 'available' } }
    Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -TimeoutSeconds 0 | Out-Null
    function Get-IndexCheckpoint { @{ sequence = 4; leaseStatus = 'unlocked'; leaseState = 'available' } }
    $rejected = $false
    try { Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -TimeoutSeconds 0 | Out-Null } catch { $rejected = $_.Exception.Message -like '*NOT verified*' }
    if (!$rejected) { throw 'Incomplete checkpoint must not pass.' }
    function Get-IndexCheckpoint { @{ sequence = 5; leaseStatus = 'locked'; leaseState = 'leased' } }
    $rejected = $false
    try { Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -TimeoutSeconds 0 | Out-Null } catch { $rejected = $_.Exception.Message -like '*NOT verified*' }
    if (!$rejected) { throw 'Unreleased ingestion lease must not pass.' }
    $initial = Get-IngestionRequirements -InitialSeed $true
    $rerun = Get-IngestionRequirements -InitialSeed $false
    if ($initial.documentCount -ne 176 -or $initial.checkpointSequence -ne 5 -or
        $rerun.documentCount -ne 0 -or $rerun.checkpointSequence -ne 0) { throw 'Initial seed/rerun requirements regression.' }
    function Invoke-SearchRequest { @{ documentCount = 183; storageSize = 2048 } }
    function Get-CommittedSourceSequence { 6 }
    function Get-IndexCheckpoint { @{ sequence = 6; leaseStatus = 'unlocked'; leaseState = 'available' } }
    Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -ExpectedDocumentCount $rerun.documentCount -ExpectedCheckpointSequence $rerun.checkpointSequence -TimeoutSeconds 0 | Out-Null
    function Invoke-SearchRequest { @{ documentCount = 159; storageSize = 2048 } }
    Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -ExpectedDocumentCount $rerun.documentCount -ExpectedCheckpointSequence $rerun.checkpointSequence -TimeoutSeconds 0 | Out-Null
    function Get-CommittedSourceSequence { 7 }
    $rejected = $false
    try { Wait-SearchIngestion -Endpoint 'https://not-contacted.invalid' -BlobServiceUri 'https://not-contacted.invalid/' -TimeoutSeconds 0 | Out-Null } catch { $rejected = $_.Exception.Message -like '*NOT verified*' }
    if (!$rejected) { throw 'Rerun checkpoint lag behind manifest must not pass.' }
    Write-Output 'PASS: parsed scripts; WhatIf had zero cloud calls; CLI/context/retry and ingestion count/budget/deadline/checkpoint/lease checks passed with local mocks.'
} finally {
    Remove-Item $testManifest -ErrorAction SilentlyContinue
    Remove-Item Function:\az -ErrorAction SilentlyContinue
}
