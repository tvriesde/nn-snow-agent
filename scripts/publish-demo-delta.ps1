#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Insert', 'Update', 'Delete', 'Unpublish', 'Shrink', 'SameTimestamp')]
    [string]$Scenario,
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\data\servicenow'),
    [string]$StorageAccountName,
    [string]$ContainerName = 'servicenow',
    [string]$SubscriptionId,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if ($Upload -and ($StorageAccountName -notmatch '^[a-z0-9]{3,24}$' -or
    $ContainerName -notmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$' -or
    $SubscriptionId -notmatch '^[a-fA-F0-9-]{36}$')) {
    throw 'Upload requires a valid StorageAccountName, ContainerName, and explicit SubscriptionId.'
}
$manifestPath = Join-Path $DataDirectory 'manifest.json'
if (-not (Test-Path $manifestPath)) { throw 'Generate the baseline fixtures first.' }
if (-not $PSCmdlet.ShouldProcess(
    $(if ($Upload) { "$StorageAccountName/$ContainerName" } else { $DataDirectory }),
    "Commit a synthetic $Scenario delta")) { return }

$lockPath = Join-Path $DataDirectory '.publish.lock'
if (Test-Path $lockPath) {
    throw 'A publisher lock already exists. Confirm no producer is running before removing only this stale lock file.'
}
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$temporaryManifest = Join-Path $DataDirectory 'manifest.pending.json'
try {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $headers = $null
    $etag = $null
    $blobBase = $null
    if ($Upload) {
        $tokenJson = az account get-access-token --subscription $SubscriptionId --resource https://storage.azure.com/ -o json --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Could not obtain a Storage access token.' }
        $token = ($tokenJson | ConvertFrom-Json).accessToken
        $headers = @{
            Authorization = "Bearer $token"
            'x-ms-version' = '2023-11-03'
            'x-ms-date' = [DateTime]::UtcNow.ToString('R')
        }
        $blobBase = "https://$StorageAccountName.blob.core.windows.net/$ContainerName"
        $response = Invoke-WebRequest -Uri "$blobBase/manifest.json" -Headers $headers
        $remote = $response.Content | ConvertFrom-Json -AsHashtable
        if ($remote.seed -ne $manifest.seed -or $remote.schemaVersion -ne 1) {
            throw 'Remote source does not match these synthetic fixtures.'
        }
        $manifest = $remote
        $etag = [string]$response.Headers.ETag[0]
        if ([string]::IsNullOrEmpty($etag)) { throw 'Remote manifest did not return an ETag.' }
    }
    for ($i = 0; $i -lt @($manifest.batches).Count; $i++) {
        if ($manifest.batches[$i].sequence -ne ($i + 1)) {
            throw 'The committed source manifest has invalid or non-contiguous sequences.'
        }
    }
    $sequence = @($manifest.batches).Count + 1
    $baseline = (Get-Content (Join-Path $DataDirectory 'baseline\kb_knowledge.json') -Raw |
        ConvertFrom-Json -AsHashtable).result
    $record = $baseline[0]
    if ($Scenario -eq 'Delete') { $record = $baseline[1] }
    $change = [ordered]@{ operation = 'upsert'; table = 'kb_knowledge' }
    if ($Scenario -eq 'Delete') {
        $change.operation = 'delete'
        $change.sys_id = $record.sys_id
    } else {
        $record.sys_mod_count = [string]$sequence
        if ($Scenario -ne 'SameTimestamp') {
            $record.sys_updated_on = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss')
        }
        switch ($Scenario) {
            'Insert' {
                $record.sys_id = [Guid]::NewGuid().ToString('N')
                $record.number = 'KB' + (90000 + $sequence).ToString('D7')
                $record.short_description = 'Employee IT Helpdesk: recovering after a browser session expires'
                $record.business_service = $baseline[8].business_service
                $record.text = '<p>Sign in again through the approved tenant. An expired browser session is not proof that the backend is down. Check live Azure evidence for mapped helpdesk resources if requests fail after sign-in.</p>'
            }
            'Update' {
                $record.text += "<p>Delta version ${sequence}: capture the UTC time and error code, then use the approved recovery process. Do not share passwords or MFA codes.</p>"
            }
            'Unpublish' { $record.workflow_state = 'draft' }
            'Shrink' { $record.text = '<p>Use the approved identity recovery process. Never share passwords or MFA codes.</p>' }
            'SameTimestamp' { $record.text += "<p>Same-timestamp change ${sequence}: sequence-based ingestion must include this updated guidance.</p>" }
        }
        $change.record = $record
    }
    $filename = '{0:D8}-{1}.json' -f $sequence, [Guid]::NewGuid().ToString('N')
    $path = Join-Path $DataDirectory "changes\$filename"
    [IO.Directory]::CreateDirectory((Join-Path $DataDirectory 'changes')) | Out-Null
    $json = @{ changes = @($change) } | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    $entry = [ordered]@{
        sequence = $sequence; path = "changes/$filename"; kind = 'delta'; table = $null
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant(); recordCount = 1
    }
    $manifest.batches = @($manifest.batches) + @($entry)
    $manifestJson = $manifest | ConvertTo-Json -Depth 12
    if ($Upload) {
        $batchHeaders = $headers.Clone()
        $batchHeaders['x-ms-blob-type'] = 'BlockBlob'
        $batchHeaders['If-None-Match'] = '*'
        Invoke-WebRequest -Method Put -Uri "$blobBase/changes/$filename" -Headers $batchHeaders `
            -InFile $path -ContentType 'application/json' | Out-Null
        $commitHeaders = $headers.Clone()
        $commitHeaders['x-ms-blob-type'] = 'BlockBlob'
        $commitHeaders['If-Match'] = $etag
        try {
            Invoke-WebRequest -Method Put -Uri "$blobBase/manifest.json" -Headers $commitHeaders `
                -Body ([Text.Encoding]::UTF8.GetBytes($manifestJson)) -ContentType 'application/json' | Out-Null
        } catch {
            throw "Manifest commit failed; the unpublished batch is ignored by the indexer. Retry with the latest manifest. $($_.Exception.Message)"
        }
    }
    [IO.File]::WriteAllText($temporaryManifest, $manifestJson, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryManifest, $manifestPath, $true)
    Write-Output "Committed synthetic $Scenario batch $sequence; a scheduled indexer tick will apply it after upload."
} finally {
    $lock.Dispose()
    [IO.File]::Delete($lockPath)
    if (Test-Path $temporaryManifest) { [IO.File]::Delete($temporaryManifest) }
}
