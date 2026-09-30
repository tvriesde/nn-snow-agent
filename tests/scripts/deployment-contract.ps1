#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'scripts\deploy.ps1'), [ref]$null, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Deployment script has parse errors.' }
$parameter = $ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -eq 'IngestionTimeoutSeconds'
}
$deadline = [int]$parameter.DefaultValue.Value
$hostSettings = Get-Content (Join-Path $root 'src\indexer\host.json') -Raw | ConvertFrom-Json
$requiredDeadline = 15 * 60 + [TimeSpan]::Parse($hostSettings.functionTimeout).TotalSeconds
if ($deadline -lt $requiredDeadline) {
    throw 'Default ingestion deadline must cover the schedule interval plus the Function execution budget.'
}
$ignored = git -C $root check-ignore .azure/ownership.json
if ($LASTEXITCODE -ne 0 -or -not $ignored) {
    throw 'Deployment ownership manifest must be ignored by Git.'
}
Write-Output 'PASS: ingestion deadline covers schedule/execution budgets and ownership state is Git-ignored.'
