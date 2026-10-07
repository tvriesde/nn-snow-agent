[CmdletBinding()]
param([switch]$Deploy)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$ownership = Get-Content (Join-Path $root '.azure\ownership.json') -Raw | ConvertFrom-Json
function Invoke-AzureJson {
    param([string[]]$Arguments)
    $text = & az @Arguments --subscription $ownership.subscriptionId --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "Azure command failed: $($Arguments[0..1] -join ' ')" }
    return (($text -join "`n") | ConvertFrom-Json)
}
$account = & az account show --only-show-errors -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $account.id -ne $ownership.subscriptionId -or $account.tenantId -ne $ownership.tenantId) {
    throw 'Azure CLI context does not match the owned deployment.'
}
$group = Invoke-AzureJson @('group', 'show', '-n', $ownership.resourceGroupName)
if ($group.tags.ownershipId -ne $ownership.ownershipId) { throw 'Resource group ownership does not match.' }
$aiAccounts = @(Invoke-AzureJson @('cognitiveservices', 'account', 'list', '-g', $ownership.resourceGroupName))
if ($aiAccounts.Count -ne 1 -or $aiAccounts[0].kind -ne 'OpenAI' -or $aiAccounts[0].location -ne 'swedencentral') {
    throw 'Expected exactly one owned Sweden Central OpenAI account.'
}
$settings = @(Invoke-AzureJson @('webapp', 'config', 'appsettings', 'list', '-g', $ownership.resourceGroupName,
    '-n', $ownership.resources.backendName))
$searchEndpoint = ($settings | Where-Object name -eq 'Search__Endpoint').value
$reference = ($settings | Where-Object name -eq 'AzureOpenAI__ApiKey').value
if ($reference -notmatch '^@Microsoft.KeyVault\(VaultName=([^;]+);SecretName=azure-openai-key\)$') {
    throw 'Expected the approved Key Vault secret reference.'
}
$vault = $Matches[1]
$search = ([uri]$searchEndpoint).Host.Split('.')[0]
$parameters = @("openAIAccountName=$($aiAccounts[0].name)", "searchServiceName=$search",
    "keyVaultName=$vault", "operatorObjectId=$($ownership.operatorObjectId)")
$template = Join-Path $root 'infra\eval-models.bicep'
$validation = Invoke-AzureJson (@('deployment', 'group', 'validate', '-g', $ownership.resourceGroupName,
    '--template-file', $template, '--parameters') + $parameters)
$preview = Invoke-AzureJson (@('deployment', 'group', 'what-if', '-g', $ownership.resourceGroupName,
    '--template-file', $template, '--parameters') + $parameters + @('--no-pretty-print'))
$allowed = @('eval-gpt-5-mini', 'eval-gpt-4-1-mini', 'eval-gpt-4-1', 'eval-judge')
foreach ($change in $preview.changes) {
    if ($change.changeType -in @('Ignore', 'NoChange')) { continue }
    if ($change.changeType -ne 'Create') { throw "Non-additive change refused: $($change.resourceId) $($change.changeType)" }
    if ($change.resourceId -match '/Microsoft.CognitiveServices/accounts/[^/]+/deployments/([^/]+)$') {
        if ($Matches[1] -notin $allowed) { throw "Unexpected model change: $($change.resourceId)" }
    } elseif ($change.resourceId -match '/Microsoft.Authorization/roleAssignments/') {
        if ($change.after.properties.principalId -ne $ownership.operatorObjectId -or
            ($change.after.properties.roleDefinitionId.Split('/')[-1] -notin
                @('1407120a-92aa-4202-b7e9-c0e197c71c8f', '4633458b-17de-408a-b874-0445c86b69e6'))) {
            throw "Unexpected role assignment: $($change.resourceId)"
        }
    } else { throw "Unexpected resource change: $($change.resourceId)" }
}
$proof = Join-Path $root '.azure\evaluation-preflight.json'
@{ timestamp = [DateTime]::UtcNow.ToString('o'); validation = $validation; whatIf = $preview } |
    ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $proof -Encoding utf8
Write-Host 'ARM validation and additive-only what-if passed.'
if ($Deploy) {
    $plan = Get-Content (Join-Path $root '.azure\deployment-plan.md') -Raw
    if ($plan -notmatch '(?m)^Status: Validated' -or $plan -notmatch 'Evaluation validation proof') {
        throw 'Run azure-validate and record validation proof before deployment.'
    }
    $deployment = Invoke-AzureJson (@('deployment', 'group', 'create', '-g', $ownership.resourceGroupName,
        '-n', 'snowdemo-evaluation-models', '--template-file', $template, '--parameters') + $parameters)
    $deployment | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $root '.azure\evaluation-deployment.json') -Encoding utf8
    if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'Evaluation deployment did not succeed.' }
    Write-Host 'Evaluation deployments and narrowly scoped local permissions provisioned.'
}
