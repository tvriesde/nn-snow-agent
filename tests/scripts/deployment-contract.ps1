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

$commonPath = Join-Path $root 'scripts\common.ps1'
$commonText = Get-Content $commonPath -Raw
$versions = @([regex]::Matches($commonText, '3\.\d+\.\d+-beta\.\d+') |
    ForEach-Object { $_.Value } | Sort-Object -Unique)
if ($versions.Count -ne 1) { throw 'MCP download, fallback and binary guard pins must agree.' }
foreach ($relative in @('scripts\deploy.ps1', 'src\frontend\src\ArchitecturePage.tsx')) {
    $pins = @([regex]::Matches((Get-Content (Join-Path $root $relative) -Raw), '3\.\d+\.\d+-beta\.\d+') |
        ForEach-Object { $_.Value } | Sort-Object -Unique)
    if ($pins.Count -ne 1 -or $pins[0] -ne $versions[0]) {
        throw "MCP pin mismatch in $relative."
    }
}
. $commonPath
if ($env:MCP_CONTRACT_LINUX_EXECUTABLE) {
    Assert-LinuxMcpExecutable $env:MCP_CONTRACT_LINUX_EXECUTABLE
}
$testBinary = Join-Path ([IO.Path]::GetTempPath()) ("mcp-invalid-artifact-$([guid]::NewGuid().ToString('N'))")
try {
    $bytes = [byte[]]::new(20)
    $bytes[0] = 0x7f; $bytes[1] = 0x45; $bytes[2] = 0x4c; $bytes[3] = 0x46
    $bytes[4] = 2; $bytes[5] = 1; $bytes[18] = 0x3e
    [IO.File]::WriteAllBytes($testBinary, $bytes)
    $rejected = $false
    try { Assert-LinuxMcpExecutable $testBinary }
    catch { $rejected = $_.Exception.Message -like '*does not match the verified*' }
    if (!$rejected) { throw 'A Linux x64 header with an unpinned checksum must be refused.' }
    $bytes[0] = 0
    [IO.File]::WriteAllBytes($testBinary, $bytes)
    $rejected = $false
    try { Assert-LinuxMcpExecutable $testBinary }
    catch { $rejected = $_.Exception.Message -like '*native Linux x64 ELF*' }
    if (!$rejected) { throw 'A non-Linux MCP executable must be refused.' }
} finally {
    if (Test-Path $testBinary) { Remove-Item -LiteralPath $testBinary }
}
Write-Output "PASS: MCP $($versions[0]) pins agree; platform and checksum guards reject invalid artifacts."
