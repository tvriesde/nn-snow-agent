#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\data\servicenow'),
    [string]$Seed = 'insurance-demo-v1',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path (Join-Path $OutputDirectory 'manifest.json')) -and -not $Force) {
    throw 'Demo data already exists. Use -Force only to intentionally regenerate local fixtures; this does not reset a deployed index.'
}
if (-not $PSCmdlet.ShouldProcess($OutputDirectory, 'Generate deterministic synthetic ServiceNow fixtures')) { return }

function Get-SourceId([string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes("$Seed/$Value")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 32).ToLowerInvariant()
}

function New-Reference([string]$Table, [string]$Key, [string]$Display) {
    $id = Get-SourceId "$Table/$Key"
    return [ordered]@{
        value = $id
        display_value = $Display
        link = "https://fictional-insurer.example.invalid/api/now/table/$Table/$id"
    }
}

$scenarios = @(
    @{ App = 'Claims Workbench'; Category = 'identity'; Title = 'Cannot sign in after a password change'; Keywords = 'login logon password expired account locked sign in';
       Steps = 'Confirm the official Claims Workbench address. Close existing browser sessions and open a new session after the password change. Use your corporate account, not a broker account. If the account is locked, contact the service desk through the approved recovery channel. Never share passwords or MFA codes.' },
    @{ App = 'Corporate Identity'; Category = 'identity'; Title = 'MFA prompts go to an old phone'; Keywords = 'authenticator lost phone multifactor reset MFA';
       Steps = 'If you still have an approved alternate factor, use it to open the corporate security-information page and update your device. If all registered factors are unavailable, request identity verification through the service desk. Do not approve prompts you did not initiate. MFA cannot be bypassed by the assistant.' },
    @{ App = 'Policy Administration'; Category = 'browser'; Title = 'Repeated SSO redirects and login loops'; Keywords = 'sign in loop single sign on SSO redirect cookies';
       Steps = 'Use the published Policy Administration URL. Check that the browser profile is signed in with the corporate identity. Try an approved private browser window to isolate stale session cookies. If that works, clear cookies only for the affected application according to corporate browser guidance. Record the time and error code for escalation.' },
    @{ App = 'Broker Portal'; Category = 'network'; Title = 'Old application URL returns HTTP 404'; Keywords = 'broken link url address page not found 404';
       Steps = 'The synthetic portal was renamed from /broker-old to /broker in the demo change record. Open the application from the corporate launcher rather than an old bookmark. Request the current approved URL from the service desk if the launcher is unavailable. This fictional URL change is not evidence about a live Azure deployment.' },
    @{ App = 'Underwriting Desktop'; Category = 'network'; Title = 'VPN connects but underwriting application does not load'; Keywords = 'VPN DNS split tunnel timeout unable connect';
       Steps = 'Confirm the approved VPN profile and corporate network connection. Reconnect the VPN once, then retry the approved application URL. Capture whether other intranet applications work and whether the error is DNS resolution or a connection timeout. Do not disable endpoint protection or manually change corporate DNS servers.' },
    @{ App = 'Document Vault'; Category = 'access'; Title = 'Access denied when opening a claim attachment'; Keywords = 'permissions forbidden attachment document 403 access denied';
       Steps = 'Check whether the claim assignment and document classification allow your team to access the attachment. Request access through the approved document-owner workflow. Do not forward protected claim files to personal email or change the classification to work around access controls. Provide the document identifier, not its contents, when escalating.' },
    @{ App = 'Actuarial Analytics'; Category = 'performance'; Title = 'Analytics is slow during month-end processing'; Keywords = 'slow latency month end batch job timeout';
       Steps = 'Check the synthetic maintenance notice and known-problem record for overlapping month-end batch processing. Reduce unnecessary concurrent exports and retry outside the published processing window when practical. Capture the report name, start time, and duration. Historical synthetic incidents do not establish the current health of a real application.' },
    @{ App = 'Finance Reporting'; Category = 'compliance'; Title = 'Report export blocked by data-loss prevention'; Keywords = 'DLP blocked export spreadsheet confidential data';
       Steps = 'Use the approved reporting workspace and restricted corporate destination. Check the export policy and classification. If a legitimate business export is blocked, ask the data owner and security team to review the policy through the approved process. Never remove protection or upload financial reports to public file-sharing sites.' },
    @{ App = 'Employee IT Helpdesk'; Category = 'availability'; Title = 'Helpdesk URL is unavailable or shows a gateway error'; Keywords = 'uptime unavailable HTTP 502 503 application down';
       Steps = 'Capture the URL, UTC time, and HTTP error. Check live Azure evidence for the mapped helpdesk frontend and backend before concluding that an outage exists. A Running resource state is not HTTP availability. Compare request errors and measured probe history. The lowest-cost Free App Service demo may cold-start or exhaust daily CPU quota; the assistant cannot restart resources.' },
    @{ App = 'Corporate VPN'; Category = 'network'; Title = 'Remote employee cannot reach internal applications'; Keywords = 'remote office network VPN certificate connection';
       Steps = 'Check device time and the approved VPN client status. Verify the device certificate using the corporate support procedure. Reconnect once and record the displayed error if the issue persists. Escalate certificate renewal to endpoint support; do not install an untrusted certificate or disable certificate validation.' },
    @{ App = 'Employee Collaboration'; Category = 'device'; Title = 'Shared printer and secure scan queue are missing'; Keywords = 'printer secure scan office queue device';
       Steps = 'Confirm the office location and approved print service. Reconnect the managed print queue using the corporate software catalog. If the scan destination is missing, ask the document-service owner to confirm your group membership. Never scan policy or claim documents to an unapproved destination.' },
    @{ App = 'Policy Document Signing'; Category = 'identity'; Title = 'Digital signing fails because a certificate expired'; Keywords = 'certificate signing expired digital signature';
       Steps = 'Record the certificate-expiry message and application name. Request renewal through the approved certificate-support workflow. Do not roll back the computer clock or bypass signature verification. Existing signed documents should be reviewed by the policy-document team rather than re-signed without authorization.' }
)
$created = '2026-09-01 08:00:00'
$updated = '2026-09-02 09:00:00'
$tables = [ordered]@{}
$kb = @()
for ($i = 0; $i -lt 60; $i++) {
    $scenario = $scenarios[$i % $scenarios.Count]
    $variant = [int][Math]::Floor($i / $scenarios.Count) + 1
    $kb += [ordered]@{
        sys_id = Get-SourceId "kb_knowledge/$i"; number = ('KB{0:D7}' -f (10000 + $i))
        sys_created_on = $created; sys_updated_on = $updated; sys_mod_count = '0'
        short_description = "$($scenario.Title) - employee guide $variant"
        text = "<h2>$($scenario.Title)</h2><p>$($scenario.Steps)</p><p>Search terms: $($scenario.Keywords).</p><p>Applies to managed corporate devices. If unresolved, provide application name, UTC time, and error code to the service desk. Synthetic insurer training scenario $variant.</p>"
        workflow_state = 'published'; audience = 'employee'; active = 'true'
        category = $scenario.Category
        business_service = New-Reference 'cmdb_ci_business_app' ($i % 12) $scenario.App
        assignment_group = New-Reference 'sys_user_group' $scenario.Category "IT $($scenario.Category) Support"
        work_notes = 'Restricted synthetic internal note: excluded from employee indexing.'
    }
}
$tables['kb_knowledge'] = $kb
$incidents = @()
for ($i = 0; $i -lt 120; $i++) {
    $scenario = $scenarios[$i % 12]
    $resolved = $i -lt 96
    $incidents += [ordered]@{
        sys_id = Get-SourceId "incident/$i"; number = ('INC{0:D7}' -f (20000 + $i))
        sys_created_on = $created; sys_updated_on = $updated; sys_mod_count = '1'
        short_description = "$($scenario.Title) - synthetic employee case $($i + 1)"
        description = "Fictional employee in regional office $($i % 6 + 1) reported this issue on a managed device."
        incident_state = $(if ($resolved) { '6' } else { '2' })
        priority = [string]($i % 3 + 2); category = $scenario.Category; subcategory = 'employee-service'
        business_service = New-Reference 'cmdb_ci_business_app' ($i % 12) $scenario.App
        cmdb_ci = New-Reference 'cmdb_ci_business_app' ($i % 12) $scenario.App
        assignment_group = New-Reference 'sys_user_group' $scenario.Category "IT $($scenario.Category) Support"
        caller_id = New-Reference 'sys_user' $i ('Synthetic Employee {0:D3}' -f ($i + 1))
        close_notes = $(if ($resolved) { "Sanitized historical resolution: $($scenario.Steps) Historical synthetic case only; verify current symptoms." } else { '' })
        work_notes = 'Restricted synthetic troubleshooting details; never searchable by employees.'
        active = $(if ($resolved) { 'false' } else { 'true' }); audience = 'employee'
    }
}
$tables['incident'] = $incidents
$problems = @()
for ($i = 0; $i -lt 20; $i++) {
    $scenario = $scenarios[$i % 12]
    $problems += [ordered]@{
        sys_id = Get-SourceId "problem/$i"; number = ('PRB{0:D7}' -f (30000 + $i))
        sys_created_on = $created; sys_updated_on = $updated; sys_mod_count = '1'
        short_description = "Known problem: $($scenario.Title)"
        description = 'Synthetic historical recurring issue; not a current outage announcement.'
        known_error = 'true'; problem_state = '104'; priority = '3'
        resolution_notes = "Approved workaround: $($scenario.Steps)"
        business_service = New-Reference 'cmdb_ci_business_app' ($i % 12) $scenario.App
        category = $scenario.Category; active = 'true'; audience = 'employee'
    }
}
$tables['problem'] = $problems
$changes = @()
for ($i = 0; $i -lt 15; $i++) {
    $scenario = $scenarios[$i % 12]
    $changes += [ordered]@{
        sys_id = Get-SourceId "change_request/$i"; number = ('CHG{0:D7}' -f (40000 + $i))
        sys_created_on = $created; sys_updated_on = $updated; sys_mod_count = '0'
        short_description = "Scheduled synthetic maintenance for $($scenario.App)"
        description = 'Approved fictional change; consult a published KB for employee-facing instructions.'
        state = '3'; approval = 'approved'; type = 'normal'; risk = '3'
        start_date = '2026-09-05 20:00:00'; end_date = '2026-09-05 21:00:00'
        business_service = New-Reference 'cmdb_ci_business_app' ($i % 12) $scenario.App
        category = $scenario.Category; active = 'true'; audience = 'employee'
    }
}
$tables['change_request'] = $changes
$applications = @()
for ($i = 0; $i -lt 12; $i++) {
    $scenario = $scenarios[$i]
    $applications += [ordered]@{
        sys_id = Get-SourceId "cmdb_ci_business_app/$i"; name = $scenario.App
        sys_created_on = $created; sys_updated_on = $updated; sys_mod_count = '0'
        short_description = "Fictional insurance employee application: $($scenario.App)"
        operational_status = '1'; business_criticality = '2 - somewhat critical'
        description = 'Synthetic CMDB record. No live Azure resource mapping is implied.'
        active = 'true'; audience = 'employee'
    }
}
$tables['cmdb_ci_business_app'] = $applications

[IO.Directory]::CreateDirectory((Join-Path $OutputDirectory 'baseline')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $OutputDirectory 'changes')) | Out-Null
$batches = @()
$sequence = 0
foreach ($table in $tables.Keys) {
    $sequence++
    $relative = "baseline/$table.json"
    $path = Join-Path $OutputDirectory "baseline\$table.json"
    $json = @{ result = @($tables[$table]) } | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    $batches += [ordered]@{
        sequence = $sequence; path = $relative; kind = 'snapshot'; table = $table
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        recordCount = @($tables[$table]).Count
    }
}
$manifest = [ordered]@{ schemaVersion = 1; seed = $Seed; synthetic = $true; batches = $batches }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output "Generated 227 synthetic ServiceNow records and 5 committed baseline batches in $OutputDirectory."
