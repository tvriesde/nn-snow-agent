#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$templatePath = [IO.Path]::GetTempFileName()
$mainPath = [IO.Path]::GetTempFileName()
$manifestPath = [IO.Path]::GetTempFileName()
try {
    foreach ($build in @(
        @{ source = 'infra\health-model.bicep'; output = $templatePath }
        @{ source = 'infra\main.bicep'; output = $mainPath }
    )) {
        & az bicep build --file (Join-Path $root $build.source) --outfile $build.output
        if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed: $($build.source)" }
    }
    $template = Get-Content $templatePath -Raw | ConvertFrom-Json -AsHashtable
    $child = @($template.resources | Where-Object type -eq 'Microsoft.Resources/deployments')[0]
    $model = @($child.properties.template.resources | Where-Object type -eq 'Microsoft.CloudHealth/healthmodels')[0]
    $authentication = @($child.properties.template.resources | Where-Object type -eq 'Microsoft.CloudHealth/healthmodels/authenticationsettings')[0]
    $reader = @($template.resources | Where-Object type -eq 'Microsoft.Authorization/roleAssignments')[0]
    if ($template.'$schema' -notlike '*subscriptionDeploymentTemplate*' -or $template.resources.Count -ne 2) { throw 'Expected standalone subscription deployment and Reader grant only.' }
    if ($model.apiVersion -ne '2026-09-01-preview' -or $model.properties.Count -ne 0 -or
        $model.identity.type -ne 'SystemAssigned' -or $model.name -notlike '*-health*' -or
        !$model.tags.ownershipId -or $model.tags.application -ne 'employee-it-helpdesk') { throw 'Model/identity/naming/ownership contract failed.' }
    if ($child.properties.template.resources.Count -ne 10 -or
        $authentication.apiVersion -ne '2026-09-01-preview' -or $authentication.name -notlike '*systemassigned*' -or
        $authentication.properties.displayName -ne 'SystemAssigned' -or
        $authentication.properties.authenticationKind -ne 'ManagedIdentity' -or
        $authentication.properties.managedIdentityName -ne 'SystemAssigned' -or !$authentication.dependsOn) { throw 'SystemAssigned authentication contract failed.' }
    if ($reader.ContainsKey('scope') -or $reader.properties.roleDefinitionId -ne "[variables('readerRoleId')]" -or
        $template.variables.readerRoleId -notlike '*acdd72a7-3385-48ef-bd42-f606fba81ae7*' -or
        $reader.properties.principalType -ne 'ServicePrincipal' -or $reader.properties.principalId -notlike '*outputs.principalId.value*' -or
        !$reader.dependsOn) { throw 'Subscription Reader scope/principal/dependency contract failed.' }
    $main = Get-Content $mainPath -Raw | ConvertFrom-Json -AsHashtable
    if (!$main.outputs.healthModelReaderRoleAssignmentId -or $main.outputs.resources.value -notlike '*healthModelPrincipalId*') { throw 'Main deployment must expose health model cleanup coordinates.' }
    $healthModule = @($main.resources | Where-Object { $_.type -eq 'Microsoft.Resources/deployments' -and $_.name -like '*-health-model*' })[0]
    if (!$healthModule.dependsOn -or !$healthModule.properties.template.outputs.readerRoleAssignmentId) { throw 'Main health module must depend on resource group and expose Reader grant.' }
    foreach ($parameter in @('backendResourceId', 'frontendResourceId', 'appServicePlanResourceId')) {
        if ($child.properties.parameters[$parameter].value -ne "[parameters('$parameter')]" -or
            $healthModule.properties.parameters[$parameter].value -notlike '*outputs.resources.value.*' -or
            $template.parameters[$parameter].defaultValue -notlike '*resourceId(*') { throw "Resource ID parameter wiring failed: $parameter" }
    }
    if (($template | ConvertTo-Json -Depth 100 -Compress) -match 'd860292c|x5fxxwmbi6pwm|2c2b1133') { throw 'Health model must not hardcode deployment-specific IDs.' }
    $content = $child.properties.template
    $entities = @($content.resources | Where-Object type -eq 'Microsoft.CloudHealth/healthmodels/entities')
    $relationships = @($content.resources | Where-Object type -eq 'Microsoft.CloudHealth/healthmodels/relationships')
    if ($entities.Count -ne 4 -or $relationships.Count -ne 4) { throw 'Export must contain exactly four entities and four relationships.' }
    $expectedEntities = @(
        @{ id = '3e4980d9-ce73-492b-892e-380e75214d85'; x = -400; y = 200; parameter = 'backendResourceId'; signal = 'fafd1899-e9dd-4933-bd07-1bc66e53145e' }
        @{ id = '42824da5-1c23-424d-88ea-6302b743a844'; x = -130; y = 80; parameter = 'frontendResourceId'; signal = '51db77a7-ac37-4843-aa4c-bea318162266' }
        @{ id = '6776ebf5-56cf-43f9-b556-f8f2f74cbeea'; x = -130; y = 480; parameter = 'appServicePlanResourceId' }
        @{ id = '-health'; x = -130; y = -120 }
    )
    foreach ($expected in $expectedEntities) {
        $entity = if ($expected.parameter) {
            @($entities | Where-Object { $_.name -like "*$($expected.id)*" })[0]
        } else {
            @($entities | Where-Object { !$_.properties.ContainsKey('signalGroups') })[0]
        }
        if (!$entity -or $entity.apiVersion -ne '2026-05-01-preview' -or $entity.properties.impact -ne 'Standard' -or
            $entity.properties.canvasPosition.x -ne $expected.x -or $entity.properties.canvasPosition.y -ne $expected.y) { throw "Entity ID/API/layout/impact mismatch: $($expected.id)" }
        if ($expected.parameter) {
            $group = $entity.properties.signalGroups.azureResource
            if ($group.azureResourceId -ne "[parameters('$($expected.parameter)')]" -or
                $entity.properties.displayName -ne "[last(split(parameters('$($expected.parameter)'), '/'))]" -or
                $entity.properties.icon.iconName -ne 'Resource' -or $group.authenticationSetting -ne 'systemassigned' -or
                !$entity.dependsOn) { throw "Entity resource/authentication wiring mismatch: $($expected.id)" }
            if ($expected.signal -and ($group.signals.Count -ne 1 -or
                $group.signals[0] -notlike "*variables('httpServerErrors')*$($expected.signal)*")) { throw 'HTTP signal ID/settings reuse mismatch.' }
        }
    }
    $http = $content.variables.httpServerErrors
    if ($http.signalKind -ne 'AzureResourceMetric' -or $http.metricNamespace -ne 'microsoft.web/sites' -or
        $http.metricName -ne 'Http5xx' -or $http.timeGrain -ne 'PT5M' -or $http.aggregationType -ne 'Maximum' -or
        $http.refreshInterval -ne 'PT1M' -or $http.dataUnit -ne 'Count' -or $http.displayName -ne 'Http Server Errors') { throw 'HTTP metric contract failed.' }
    $planSignals = @($entities | Where-Object name -like '*6776ebf5*')[0].properties.signalGroups.azureResource.signals
    if ($planSignals.Count -ne 2) { throw 'Plan must have exactly CPU and memory signals.' }
    $expectedSignals = @(
        @{ actual = $http; degraded = 0; unhealthy = 0 }
        @{ actual = $planSignals[0]; id = '5cd33081-eeb2-4fff-b4c7-8926f9ad2caa'; metric = 'CpuPercentage'; displayName = 'CPU Percentage'; grain = 'PT5M'; degraded = 80; unhealthy = 95 }
        @{ actual = $planSignals[1]; id = '505015b0-9055-41f0-96ba-88ec0a64d19b'; metric = 'MemoryPercentage'; displayName = 'Memory Percentage'; grain = 'PT1M'; degraded = 75; unhealthy = 90 }
    )
    foreach ($expected in $expectedSignals) {
        $signal = $expected.actual
        if ($expected.id -and ($signal.name -ne $expected.id -or $signal.metricName -ne $expected.metric -or
            $signal.displayName -ne $expected.displayName -or $signal.metricNamespace -ne 'microsoft.web/serverfarms' -or
            $signal.timeGrain -ne $expected.grain -or $signal.aggregationType -ne 'Average' -or
            $signal.dataUnit -ne 'Percent' -or $signal.refreshInterval -ne 'PT1M' -or
            $signal.signalKind -ne 'AzureResourceMetric')) { throw 'Plan metric contract failed.' }
        foreach ($rule in @('degraded', 'unhealthy')) {
            $actualRule = $signal.evaluationRules["${rule}Rule"]
            if ($actualRule.operator -ne 'GreaterThan' -or $actualRule.threshold -ne $expected[$rule]) { throw "Exact metric threshold contract failed: $rule" }
        }
    }
    $expectedRelationships = @(
        @{ id = '3e4980d9-ce73-492b-892e-380e--6776ebf5-56cf-43f9-b556-f8f2'; parent = '3e4980d9-ce73-492b-892e-380e75214d85'; child = '6776ebf5-56cf-43f9-b556-f8f2f74cbeea'; displayName = 'IsHostedWithin' }
        @{ id = '42824da5-1c23-424d-88ea-6302--6776ebf5-56cf-43f9-b556-f8f2'; parent = '42824da5-1c23-424d-88ea-6302b743a844'; child = '6776ebf5-56cf-43f9-b556-f8f2f74cbeea'; displayName = 'IsHostedWithin' }
        @{ id = 'c25edd86-963b-451a-a960-103f8ae7b72c'; parent = '-health'; child = '42824da5-1c23-424d-88ea-6302b743a844' }
        @{ id = 'df7ec38a-7345-4e15-ae58-65970aaf42ed'; parent = '42824da5-1c23-424d-88ea-6302b743a844'; child = '3e4980d9-ce73-492b-892e-380e75214d85' }
    )
    foreach ($expected in $expectedRelationships) {
        $relationship = @($relationships | Where-Object name -like "*$($expected.id)*")[0]
        if (!$relationship -or $relationship.apiVersion -ne '2026-05-01-preview' -or
            $relationship.properties.parentEntityName -notlike "*$($expected.parent)*" -or
            $relationship.properties.childEntityName -ne $expected.child -or
            $relationship.properties.displayName -ne $expected.displayName -or $relationship.dependsOn.Count -lt 3) { throw "Relationship topology/dependencies mismatch: $($expected.id)" }
    }

    $script:subscriptionId = 'test-subscription'
    $script:groupId = "/subscriptions/$script:subscriptionId/resourceGroups/test-rg"
    $script:mcpRoleId = "/subscriptions/$script:subscriptionId/providers/Microsoft.Authorization/roleAssignments/mcp"
    $script:healthRoleId = "/subscriptions/$script:subscriptionId/providers/Microsoft.Authorization/roleAssignments/health"
    $script:principalId = 'health-principal'
    $global:healthModelTestData = @{
        subscriptionId = $script:subscriptionId
        groupId = $script:groupId
        mcpRoleId = $script:mcpRoleId
        healthRoleId = $script:healthRoleId
        principalId = $script:principalId
        deleted = [Collections.Generic.List[string]]::new()
    }
    function global:az {
        $global:LASTEXITCODE = 0
        $data = $global:healthModelTestData
        $command = "$($args[0]) $($args[1])"
        $result = switch ($command) {
            'account show' { @{ id = $data.subscriptionId; tenantId = 'test-tenant' } }
            'group exists' { $true }
            'group show' { @{ id = $data.groupId; tags = @{ ownershipId = 'test-owner'; application = 'employee-it-helpdesk' } } }
            'resource list' { ,@{ id = "$($data.groupId)/providers/Microsoft.CloudHealth/healthmodels/test-health"; tags = @{ ownershipId = 'test-owner' } } }
            'resource show' { @{ identity = @{ principalId = $data.principalId } } }
            'role assignment' {
                switch ($args[2]) {
                    'list' {
                        $roles = @(
                            @{ id = $data.mcpRoleId; principalId = 'mcp-principal'; scope = "/subscriptions/$($data.subscriptionId)"; roleDefinitionId = '/roles/acdd72a7-3385-48ef-bd42-f606fba81ae7' }
                            @{ id = $data.healthRoleId; principalId = $data.principalId; scope = "/subscriptions/$($data.subscriptionId)"; roleDefinitionId = '/roles/acdd72a7-3385-48ef-bd42-f606fba81ae7' }
                        )
                        $assigneeIndex = [Array]::IndexOf(@($args), '--assignee')
                        if ($assigneeIndex -ge 0) { $roles = @($roles | Where-Object principalId -eq $args[$assigneeIndex + 1]) }
                        $roles
                    }
                    'delete' { $data.deleted.Add($args[[Array]::IndexOf(@($args), '--ids') + 1]) }
                    default { throw 'Unexpected role command in cleanup test.' }
                }
            }
            'group delete' {
                if (!$data.deleted.Contains($data.healthRoleId)) { throw 'Health Reader must be removed before the group.' }
                $data.deleted.Add('group')
            }
            default { throw "Unexpected Azure command in cleanup test: $command" }
        }
        ConvertTo-Json -InputObject $result -Depth 10 -Compress
    }
    foreach ($scenario in @('journaled', 'older-manifest', 'wrong-principal')) {
        $manifest = @{
            version = 1; ownershipId = 'test-owner'; subscriptionId = $script:subscriptionId; tenantId = 'test-tenant'
            resourceGroupName = 'test-rg'; resourceGroupId = $script:groupId; readerRoleAssignmentId = $script:mcpRoleId
            temporaryRoleAssignmentIds = @(); resources = @{ mcpPrincipalId = 'mcp-principal' }
        }
        if ($scenario -ne 'older-manifest') {
            $manifest.healthModelReaderRoleAssignmentId = $script:healthRoleId
            $manifest.resources.healthModelPrincipalId = if ($scenario -eq 'wrong-principal') { 'foreign-principal' } else { $script:principalId }
        }
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath
        $global:healthModelTestData.deleted.Clear()
        $rejected = $false
        try { & (Join-Path $root 'scripts\delete.ps1') -OwnershipManifest $manifestPath -Confirm:$false }
        catch {
            if ($scenario -ne 'wrong-principal' -or $_.Exception.Message -ne 'Reader assignment ownership mismatch.') { throw }
            $rejected = $true
        }
        if ($scenario -eq 'wrong-principal') {
            if (!$rejected -or $global:healthModelTestData.deleted.Contains($script:healthRoleId) -or $global:healthModelTestData.deleted.Contains('group')) { throw 'Foreign health principal must block deletion.' }
        } elseif (($global:healthModelTestData.deleted -join ',') -ne "$script:mcpRoleId,$script:healthRoleId,group") { throw "Reader cleanup ordering failed: $scenario" }
    }
    Write-Output 'PASS: exported health model entities/signals/layout/relationships, authentication, subscription Reader, main wiring, journaled/partial cleanup and ownership rejection.'
} finally {
    Remove-Item Function:\az -ErrorAction SilentlyContinue
    Remove-Variable healthModelTestData -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $templatePath, $mainPath, $manifestPath -Force
}
