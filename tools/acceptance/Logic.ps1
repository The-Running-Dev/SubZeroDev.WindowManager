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

# Reads the TaskbarProbe client log. Format: "<ISO>Z qpc=<n> run=<n> <event> <detail>". Counts only what
# the log states; whether the host's answers match Explorer's needs a baseline this script cannot take.
function Get-TaskbarProbeFacts {
    param([string[]] $Lines)
    $facts = [ordered]@{ Lines = 0; Readiness = $false; SequencesStarted = 0; SequencesEnded = 0; NativeCalls = 0; FailedCalls = 0; Deadlines = 0; CleanEnd = $false; Runs = 0; FailedDetail = @() }
    foreach ($line in @($Lines)) {
        if ($line -notmatch '^\S+Z\s+qpc=\d+\s+run=\d+\s+(?<event>\S+)\s*(?<detail>.*)$') { continue }
        $facts.Lines++
        $event = $Matches['event']; $detail = $Matches['detail']
        if ($event -eq 'TaskbarButtonCreated-received') { $facts.Readiness = $true }
        elseif ($event -eq 'sequence-start') { $facts.SequencesStarted++ }
        elseif ($event -eq 'sequence-end') { $facts.SequencesEnded++ }
        elseif ($event -eq 'deadline') { $facts.Deadlines++ }
        elseif ($event -eq 'end') {
            $facts.CleanEnd = $true
            if ($detail -match 'runs=(\d+)') { $facts.Runs = [int]$Matches[1] }
        }
        if ($event -like 'SetOverlayIcon*' -or $event -like 'SetProgress*') {
            $facts.NativeCalls++
            if ($detail -match 'hr=0x(?<hr>[0-9A-Fa-f]+)' -and [Convert]::ToUInt32($Matches['hr'], 16) -ne 0) {
                $facts.FailedCalls++
                $facts.FailedDetail += ($event + ' ' + $detail)
            }
        }
    }
    return [pscustomobject]$facts
}

# Reads the host's opt-in taskbar trace (JSON lines with a start record and, on orderly disposal, an end record).
function Get-TraceFacts {
    param([string[]] $Lines)
    $facts = [ordered]@{ Started = $false; Ended = $false; Messages = 0; Eligible = 0; Captured = 0; Dropped = 0; Distinct = @(); Unparsable = 0 }
    $seen = @{}
    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json } catch { $facts.Unparsable++; continue }
        if (-not $record -or -not $record.type) { $facts.Unparsable++; continue }
        switch ([string]$record.type) {
            'start' { $facts.Started = $true }
            'end' { $facts.Ended = $true; $facts.Eligible = [int]$record.eligible; $facts.Captured = [int]$record.captured; $facts.Dropped = [int]$record.dropped }
            'message' { $facts.Messages++; if ($record.message) { $seen[[string]$record.message] = 1 + [int]$seen[[string]$record.message] } }
        }
    }
    $facts.Distinct = @($seen.Keys | Sort-Object | ForEach-Object { '{0} x{1}' -f $_, $seen[$_] })
    return [pscustomobject]$facts
}

# Never a Pass or a Fail on its own: the question is whether the host answers like Explorer does, and the
# client-side facts only show that the calls were accepted. The Baseline is the same probe run under Explorer.
# The trace is read while the host is alive, so a missing end record is expected and not a reason.
function Get-TaskbarTransportVerdict {
    param($Probe, $Trace, [bool] $TraceFailedInLog, $Baseline)
    $reasons = @()
    if (-not $Probe -or $Probe.Lines -eq 0) { return [pscustomobject]@{ Verdict = 'Inconclusive'; Reason = 'the probe produced no log' } }
    if (-not $Probe.Readiness) { $reasons += 'the probe never received TaskbarButtonCreated' }
    if (-not $Probe.CleanEnd) { $reasons += 'the probe log has no end record' }
    if ($Probe.FailedCalls -gt 0) { $reasons += ('{0} native calls returned a failure HRESULT' -f $Probe.FailedCalls) }
    if ($Probe.Deadlines -gt 0) { $reasons += ('{0} deadlines passed' -f $Probe.Deadlines) }
    if ($TraceFailedInLog) { $reasons += 'the host logged TaskbarTraceFailed' }
    if ($Trace) {
        if (-not $Trace.Started) { $reasons += 'the host trace has no start record' }
        elseif ($Trace.Messages -eq 0 -and $Probe.NativeCalls -gt 0) { $reasons += 'the host trace recorded no taskbar message during the probe run' }
    }
    else { $reasons += 'no host trace file' }
    $matched = $false
    if ($Baseline) {
        $differs = @()
        if ([bool]$Baseline.Readiness -ne [bool]$Probe.Readiness) { $differs += 'readiness' }
        if ([int]$Baseline.NativeCalls -ne [int]$Probe.NativeCalls) { $differs += 'native call count' }
        if ([int]$Baseline.FailedCalls -ne [int]$Probe.FailedCalls) { $differs += 'failed call count' }
        if ([int]$Baseline.SequencesEnded -ne [int]$Probe.SequencesEnded) { $differs += 'completed sequences' }
        if ($differs.Count) { $reasons += ('differs from the Explorer baseline in ' + ($differs -join ', ')) } else { $matched = $true }
    }
    else { $reasons += 'no Explorer baseline from the coexistence run' }
    if ($reasons.Count) { $text = ($reasons -join '; ') }
    elseif ($matched) { $text = 'client-visible results (readiness, call count, HRESULTs) match the Explorer baseline and the host trace holds the messages it received; whether the host draws the progress and overlay on the button needs a person looking at the taskbar' }
    else { $text = 'the run was clean' }
    return [pscustomobject]@{ Verdict = 'Inconclusive'; Reason = $text }
}

# Z-order helpers take the EnumWindows list (top to bottom) as objects with Handle and Visible.
# "Dropped" means every panel that is still visible lies below the fixture; "above" is the opposite.
function Get-PanelZState {
    param([object[]] $Windows, [long[]] $Panels, [long] $Fixture)
    $list = @($Windows)
    $fixtureIndex = -1
    for ($i = 0; $i -lt $list.Count; $i++) { if ([long]$list[$i].Handle -eq $Fixture) { $fixtureIndex = $i; break } }
    if ($fixtureIndex -lt 0) { return 'unknown' }
    $found = 0; $above = 0; $visible = 0
    for ($i = 0; $i -lt $list.Count; $i++) {
        if ($Panels -notcontains [long]$list[$i].Handle) { continue }
        $found++
        if ($list[$i].Visible) { $visible++; if ($i -lt $fixtureIndex) { $above++ } }
    }
    if (-not $found) { return 'unknown' }
    if ($visible -eq 0) { return 'hidden' }
    if ($above -eq $visible) { return 'above' }
    if ($above -eq 0) { return 'below' }
    return 'mixed'
}

# Swaps the activePreset string in settings.json text without re-serialising the rest.
function Set-PresetInSettingsText {
    param([string] $Text, [string] $PresetId)
    $pattern = '("activePreset"\s*:\s*")[^"]*(")'
    if ($Text -notmatch $pattern) { return $null }
    return [regex]::Replace($Text, $pattern, { param($m) $m.Groups[1].Value + $PresetId + $m.Groups[2].Value })
}

# Everything the suite cannot settle. Reported as Skipped observations and listed in the report.
function Get-StillManual {
    return @(
        'S24.1 / S24.9 and issue #28: notification certificate trust, notification access and the switch need the real machine and its certificate store',
        'S27.4: radio control from an unpackaged process, DDC/CI brightness and the built-in panel need real hardware',
        'S29.3: whether the taskbar button visibly draws progress and the overlay; the run compares the probe with an Explorer baseline and the host trace, but only looking at the taskbar settles it',
        'S29.4: jump lists beyond the readable subset need real applications with their own jump lists',
        'S30 / S31 / S14.4 judgement calls: whether thumbnails, menus and the two sample presets look right in the attached screenshots',
        'A real second monitor and 150 % DPI: a VM has one virtual display and no supported way to change its scale unattended',
        'The sign-in password if the guest is not set to sign in automatically',
        'A week of daily use and the three open design questions'
    )
}
