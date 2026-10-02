#requires -Version 7.2
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$OwnershipManifest = '.azure\ownership.json',
    [switch]$DeleteOwnedEntraApplications,
    [string]$EntraDeletionConfirmation
)
. "$PSScriptRoot\common.ps1"
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (!(Test-Path $OwnershipManifest)) {
        if ($WhatIfPreference) { Write-Output 'WhatIf: no ownership manifest; nothing can be selected for deletion.'; return }
        throw 'Ownership manifest required. Resource groups are never selected by name or prefix alone.'
    }
    $manifest = Get-Content $OwnershipManifest -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.version -ne 1 -or !$manifest.ownershipId -or !$manifest.subscriptionId -or !$manifest.tenantId) { throw 'Invalid ownership manifest.' }
    if (!$PSCmdlet.ShouldProcess("$($manifest.subscriptionId)/$($manifest.resourceGroupName)", 'Verify ownership, remove subscription Reader and temporary scoped grants, then delete owned resource group (no Key Vault purge)')) { return }
    Assert-AzureContext $manifest.subscriptionId $manifest.tenantId
    $groupExists = Invoke-Az @('group', 'exists', '--name', $manifest.resourceGroupName)
    if ($groupExists) { Assert-OwnedGroup $manifest }
    $readerAssignments = @(
        @{ id = $manifest.readerRoleAssignmentId; principalKey = 'mcpPrincipalId' }
        @{ id = $manifest['healthModelReaderRoleAssignmentId']; principalKey = 'healthModelPrincipalId' }
    )
    foreach ($assignment in $readerAssignments) {
        if (!$assignment.id) { continue }
        $expectedPrefix = "/subscriptions/$($manifest.subscriptionId)/providers/Microsoft.Authorization/roleAssignments/"
        if (!$assignment.id.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid subscription role assignment scope.' }
        $roles = @(Invoke-Az @('role', 'assignment', 'list', '--scope', "/subscriptions/$($manifest.subscriptionId)") |
            Where-Object { $_.id -ieq $assignment.id })
        if ($roles.Count) {
            if (!$manifest.resources -or !$manifest.resources[$assignment.principalKey] -or $roles[0].principalId -ne $manifest.resources[$assignment.principalKey] -or $roles[0].roleDefinitionId -notlike '*/acdd72a7-3385-48ef-bd42-f606fba81ae7') { throw 'Reader assignment ownership mismatch.' }
            Invoke-Az @('role', 'assignment', 'delete', '--ids', $assignment.id) | Out-Null
        }
    }
    if ($groupExists -and !$manifest.readerRoleAssignmentId) {
        # A failed ARM deployment may have created Reader without returning outputs.
        $identities = @(Invoke-Az @('identity', 'list', '--resource-group', $manifest.resourceGroupName) |
            Where-Object { $_.tags.ownershipId -eq $manifest.ownershipId })
        foreach ($identity in $identities) {
            $roles = @(Invoke-Az @('role', 'assignment', 'list', '--assignee', $identity.principalId, '--scope', "/subscriptions/$($manifest.subscriptionId)") |
                Where-Object { $_.scope -ieq "/subscriptions/$($manifest.subscriptionId)" -and $_.roleDefinitionId -like '*/acdd72a7-3385-48ef-bd42-f606fba81ae7' })
            foreach ($role in $roles) { Invoke-Az @('role', 'assignment', 'delete', '--ids', $role.id) | Out-Null }
        }
    }
    if ($groupExists -and !$manifest['healthModelReaderRoleAssignmentId']) {
        # Discover the system identity when a partial deployment did not return outputs.
        $models = @(Invoke-Az @('resource', 'list', '--resource-group', $manifest.resourceGroupName, '--resource-type', 'Microsoft.CloudHealth/healthmodels') |
            Where-Object { $_.tags.ownershipId -eq $manifest.ownershipId })
        foreach ($model in $models) {
            $actual = Invoke-Az @('resource', 'show', '--ids', $model.id, '--api-version', '2026-09-01-preview')
            if (!$actual.identity.principalId) { throw 'Owned health model has no system-assigned principal.' }
            $roles = @(Invoke-Az @('role', 'assignment', 'list', '--assignee', $actual.identity.principalId, '--scope', "/subscriptions/$($manifest.subscriptionId)") |
                Where-Object { $_.scope -ieq "/subscriptions/$($manifest.subscriptionId)" -and $_.roleDefinitionId -like '*/acdd72a7-3385-48ef-bd42-f606fba81ae7' })
            foreach ($role in $roles) { Invoke-Az @('role', 'assignment', 'delete', '--ids', $role.id) | Out-Null }
        }
    }
    foreach ($id in @($manifest.temporaryRoleAssignmentIds)) {
        if (!$id.StartsWith("$($manifest.resourceGroupId)/", [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary role assignment lies outside owned group.' }
        $roles = @(Invoke-Az @('role', 'assignment', 'list', '--all') | Where-Object { $_.id -ieq $id })
        if ($roles.Count) {
            $allowed = @('ba92f5b4-2d11-453d-a403-e96b0029c9fe', '7ca78c08-252a-4471-8644-bb5ff32d4ba0', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
            if (!$manifest.ContainsKey('operatorObjectId') -or $roles[0].principalId -ne $manifest.operatorObjectId -or ($roles[0].roleDefinitionId -split '/')[-1] -notin $allowed) { throw 'Temporary role assignment ownership mismatch.' }
            Invoke-Az @('role', 'assignment', 'delete', '--ids', $id) | Out-Null
        }
    }
    if ($groupExists) { Invoke-Az @('group', 'delete', '--name', $manifest.resourceGroupName, '--yes') | Out-Null }
    if ($DeleteOwnedEntraApplications) {
        if ($EntraDeletionConfirmation -ne $manifest.ownershipId) { throw 'Separate Entra confirmation required: pass EntraDeletionConfirmation equal to manifest ownershipId. No registrations were deleted.' }
        foreach ($app in @($manifest.ownedApplications)) {
            if ($PSCmdlet.ShouldProcess($app.clientId, 'Delete explicitly owned Entra application registration (separate from resource group)')) {
                $matches = @(Invoke-Az @('ad', 'app', 'list', '--filter', "appId eq '$($app.clientId)'"))
                if (!$matches.Count) { continue }
                if ($matches.Count -ne 1) { throw 'Ambiguous Entra registration lookup.' }
                $actual = $matches[0]
                if ($actual.id -ne $app.objectId -or $actual.appId -ne $app.clientId -or $actual.displayName -ne $app.displayName -or !$actual.displayName.Contains("[$($manifest.ownershipId)]")) { throw 'Entra ownership mismatch.' }
                Invoke-Az @('ad', 'app', 'delete', '--id', $app.objectId) | Out-Null
            }
        }
    }
    Write-Output 'Owned Azure resources deleted. Key Vault is soft-deleted, not purged. Ownership manifest retained for audit/retry.'
} finally { Pop-Location }
