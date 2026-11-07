# Pure decision logic for the scripted VM acceptance suite. No desktop, registry or process
# access, so CI can unit-test it (tools/tests/Test-AcceptanceAuto.ps1). Windows PowerShell 5.1 compatible.

# Decides from WMI facts whether this machine is a virtual machine. A hypervisor alone is
# not enough: a development PC with VBS or the Hyper-V role also reports one.
function Test-VmIdentity {
    param([string] $Manufacturer, [string] $Model, [bool] $HypervisorPresent)
    $text = ($Manufacturer + ' ' + $Model)
    $known = $text -match 'Virtual Machine|VMware|VirtualBox|innotek|QEMU|KVM|Parallels|Xen|HVM domU|Bochs|Google Compute|Amazon EC2'
    return [bool]($HypervisorPresent -and $known)
}

# Reduces repeated attempts to one verdict. Never Pass without every required success, and
# attempts that could not be observed never count as successes.
function Get-Verdict {
    param([int] $Attempts, [int] $Successes, [int] $Required = 0, [int] $Unobservable = 0)
    if ($Required -le 0) { $Required = $Attempts }
    if ($Attempts -le 0) { return 'Inconclusive' }
    if ($Successes -ge $Required) { return 'Pass' }
    if ($Unobservable -gt 0 -and ($Successes + $Unobservable) -ge $Required) { return 'Inconclusive' }
    return 'Fail'
}

# Parses "<ISO8601> <Level> [area] message" host-log lines; $null when the line is not one.
function ConvertFrom-HostLogLine {
    param([string] $Line)
    if ($Line -match '^(?<t>\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|[+-]\d{2}:\d{2})?)\s+(?<l>Info|Warning|Error)\s+\[(?<a>[^\]]*)\]\s?(?<m>.*)$') {
        $when = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse($Matches['t'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$when)) {
            return [pscustomobject]@{ When = $when; Level = $Matches['l']; Area = $Matches['a']; Message = $Matches['m']; Raw = $Line }
        }
    }
    return $null
}

# The resumable sequence. Replacement phases exist only when asked for; each phase is entered
# once, in order, and 'restore-check' always follows 'replacement-run' so the guest returns to Explorer.
function Get-NextPhase {
    param([string] $Phase, [bool] $IncludeReplacement)
    switch ($Phase) {
        'new' { return 'coexistence' }
        'coexistence' { if ($IncludeReplacement) { return 'replacement-arm' } else { return 'done' } }
        'replacement-arm' { return 'replacement-run' }
        'replacement-run' { return 'restore-check' }
        'restore-check' { return 'done' }
        'done' { return 'done' }
        default { throw "Unknown phase '$Phase'." }
    }
}

# Maps a suite verdict onto the manual runner's -Result vocabulary. Nothing unobserved becomes a Pass.
function ConvertTo-RunnerResult {
    param([string] $Verdict)
    switch ($Verdict) {
        'Pass' { return 'Pass' }
        'Fail' { return 'Fail' }
        'Inconclusive' { return 'Blocked' }
        default { return 'NotRun' }
    }
}

# Which manual-runner test a scripted gate belongs to. Gates re-run under the replacement shell
# carry a suffix and are recorded as 'Other' so they never blur into the coexistence result.
function Get-RunnerTest {
    param([string] $Gate)
    switch -Regex ($Gate) {
        '^Coexistence$' { return 'Coexistence' }
        '^S28$' { return 'WinX' }
        '^S29\.2$' { return 'Hover' }
        '^S31$' { return 'AltTab' }
        '^S30$' { return 'Desktop' }
        '^ExplorerRestore$' { return 'ExplorerRestore' }
        '^(Replacement|WaysBack)$' { return 'ReplacementSignIn' }
        default { return 'Other' }
    }
}

# One verdict per gate from its checks: any Fail fails the gate; a Pass needs every check to pass;
# otherwise it is inconclusive; all-skipped stays skipped.
function Get-GateVerdict {
    param([string[]] $Verdicts)
    $v = @($Verdicts)
    if (-not $v.Count) { return 'Skipped' }
    if ($v -contains 'Fail') { return 'Fail' }
    if (@($v | Where-Object { $_ -ne 'Pass' }).Count -eq 0) { return 'Pass' }
    if (@($v | Where-Object { $_ -ne 'Skipped' }).Count -eq 0) { return 'Skipped' }
    if ($v -contains 'Pass' -or $v -contains 'Inconclusive') { return 'Inconclusive' }
    return 'Skipped'
}

# Markdown summary of the observations, followed by the items no script can settle.
function Get-AcceptanceReport {
    param([object[]] $Records, [string[]] $StillManual, [hashtable] $Facts)
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add('# Scripted acceptance report')
    $lines.Add('')
    foreach ($key in ($Facts.Keys | Sort-Object)) { $lines.Add(('- {0}: {1}' -f $key, $Facts[$key])) }
    $lines.Add('')
    $lines.Add('A Pass here means the scripted observation held. Inconclusive means the script could not observe it and a person must look at the attached evidence. Nothing is inferred.')
    $lines.Add('')
    $lines.Add('| Gate | Verdict | Checks |')
    $lines.Add('| --- | --- | --- |')
    $byGate = @($Records | Group-Object gate | Sort-Object Name)
    foreach ($group in $byGate) {
        $counts = ($group.Group | Group-Object verdict | ForEach-Object { '{0} {1}' -f $_.Count, $_.Name }) -join ', '
        $lines.Add(('| {0} | {1} | {2} |' -f $group.Name, (Get-GateVerdict @($group.Group | ForEach-Object { $_.verdict })), $counts))
    }
    $lines.Add('')
    $lines.Add('## Checks')
    $lines.Add('')
    foreach ($r in $Records) {
        $lines.Add(('- **{0}** {1} / {2}: {3}' -f $r.verdict, $r.gate, $r.check, $r.detail))
    }
    $lines.Add('')
    $lines.Add('## Still manual')
    $lines.Add('')
    foreach ($item in $StillManual) { $lines.Add('- ' + $item) }
    return ($lines -join "`n")
}

# Everything the suite cannot settle. Reported as Skipped observations and listed in the report.
function Get-StillManual {
    return @(
        'S24.1 / S24.9 and issue #28: notification certificate trust, notification access and the switch are yours to do on the real machine',
        'S27.4: radio control from an unpackaged process, DDC/CI brightness and the built-in panel need real hardware',
        'S29.3: badges need the native TaskbarProbe experiment (docs/taskbar-backchannel-experiment.md); the VM package does not ship the probe',
        'S29.4: jump lists beyond the readable subset need real applications with their own jump lists',
        'S30 / S31 judgement calls: whether thumbnails and menus look right in the attached screenshots',
        'Real second monitor, 150 % DPI and full-screen applications (docs/test-matrix.md) need hardware the VM lacks',
        'The sign-in password if the guest is not set to sign in automatically',
        'A week of daily use and the three open design questions'
    )
}
