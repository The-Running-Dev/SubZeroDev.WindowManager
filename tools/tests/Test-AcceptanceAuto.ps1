# Unit-tests the pure decision logic of the scripted VM acceptance suite and statically checks the
# scripts. Touches no desktop, registry, process or input, so it is safe on any machine and in CI.
# Run with Windows PowerShell 5.1 and with PowerShell 7.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tools = Join-Path $repo 'tools'
$modules = Join-Path $tools 'acceptance'
$files = @(
    (Join-Path $modules 'Logic.ps1'), (Join-Path $modules 'Common.ps1'), (Join-Path $modules 'Probes.ps1'),
    (Join-Path $tools 'Invoke-AcceptanceAuto.ps1')
)

function Assert-Equal($Actual, $Expected, [string] $What) {
    if ("$Actual" -ne "$Expected") { throw "$What : expected '$Expected', got '$Actual'" }
}
function Assert-True($Value, [string] $What) { if (-not $Value) { throw "Not true: $What" } }
function Pass([string] $Message) { Write-Host "PASS: $Message" }

# ---------------------------------------------------------------- static checks

$asts = @{}
foreach ($file in $files) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ("Parse errors in $file`n" + ($errors | Out-String)) }
    $asts[$file] = $ast
    $modern = @($tokens | Where-Object { @('AndAnd', 'OrOr', 'QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionMark', 'QuestionLBracket') -contains $_.Kind.ToString() })
    if ($modern.Count) { throw "$file uses PowerShell 7-only syntax ($($modern[0].Kind) at line $($modern[0].Extent.StartLineNumber)); the guest runs Windows PowerShell 5.1." }
}
Pass 'all four scripts parse and avoid PowerShell 7-only operators.'

$defined = @{}
foreach ($file in $files) {
    foreach ($f in $asts[$file].FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) { $defined[$f.Name] = $true }
}
$unknown = @()
foreach ($file in $files) {
    foreach ($c in $asts[$file].FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true)) {
        $first = $c.CommandElements[0]
        if ($first -isnot [Management.Automation.Language.StringConstantExpressionAst]) { continue }
        $name = $first.Value
        if ($defined.ContainsKey($name)) { continue }
        if (Get-Command $name -ErrorAction SilentlyContinue) { continue }
        $unknown += "$([IO.Path]::GetFileName($file)):$($c.Extent.StartLineNumber) $name"
    }
}
if ($unknown.Count) { throw ("Commands that are neither defined nor installed:`n" + ($unknown -join "`n")) }
Pass 'every command the scripts call is defined in them or installed.'

foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file)
    if ($text -match '\$env:SUBZERODEV_WM_TEST_HOOK\s*=') { throw "$file sets the test hook; the real-desktop suite must not." }
}
$orchestrator = [IO.File]::ReadAllText((Join-Path $tools 'Invoke-AcceptanceAuto.ps1'))
foreach ($name in 'TaskbarTransport', 'ShowDesktop', 'FullScreen', 'Samples') {
    Assert-True ($orchestrator -match "'$name'") "the orchestrator accepts -Only $name"
}
Assert-True ($orchestrator -match 'Remove-TaskbarTraceVariable') 'the orchestrator removes the taskbar trace variable'
Assert-True ([regex]::Matches($orchestrator, 'Remove-TaskbarTraceVariable').Count -ge 4) 'the trace variable is removed on every way out'
Pass 'the orchestrator exposes the new probes and cleans up the trace variable.'
$common = [IO.File]::ReadAllText((Join-Path $modules 'Common.ps1'))
Assert-True ($common -match "if \(\`$env:SUBZERODEV_WM_TEST_HOOK\) \{ throw") 'the guard refuses to run with the test hook set'
Pass 'the suite never sets the test hook and refuses to run with it set.'

# ---------------------------------------------------------------- pure logic

. (Join-Path $modules 'Logic.ps1')

Assert-Equal (Test-VmIdentity 'Microsoft Corporation' 'Virtual Machine' $true) 'True' 'Hyper-V guest'
Assert-Equal (Test-VmIdentity 'VMware, Inc.' 'VMware7,1' $true) 'True' 'VMware guest'
Assert-Equal (Test-VmIdentity 'innotek GmbH' 'VirtualBox' $true) 'True' 'VirtualBox guest'
Assert-Equal (Test-VmIdentity 'QEMU' 'Standard PC (Q35 + ICH9, 2009)' $true) 'True' 'QEMU guest'
Assert-Equal (Test-VmIdentity 'Dell Inc.' 'XPS 15 9530' $true) 'False' 'dev PC with VBS reports a hypervisor but is not a VM'
Assert-Equal (Test-VmIdentity 'Microsoft Corporation' 'Surface Laptop 5' $true) 'False' 'Surface with VBS'
Assert-Equal (Test-VmIdentity 'VMware, Inc.' 'VMware7,1' $false) 'False' 'a VM name without a hypervisor flag is not trusted'
Pass 'Test-VmIdentity needs both a hypervisor and a VM manufacturer or model.'

Assert-Equal (Get-Verdict -Attempts 20 -Successes 20) 'Pass' 'all succeed'
Assert-Equal (Get-Verdict -Attempts 20 -Successes 19) 'Fail' 'one loss fails the gate'
Assert-Equal (Get-Verdict -Attempts 20 -Successes 12 -Unobservable 8) 'Inconclusive' 'unobservable attempts could close the gap'
Assert-Equal (Get-Verdict -Attempts 20 -Successes 10 -Unobservable 2) 'Fail' 'unobservable attempts cannot close the gap'
Assert-Equal (Get-Verdict -Attempts 0 -Successes 0) 'Inconclusive' 'nothing attempted'
Assert-Equal (Get-Verdict -Attempts 10 -Successes 8 -Required 8) 'Pass' 'explicit threshold'
Assert-Equal (Get-Verdict -Attempts 10 -Successes 7 -Required 8) 'Fail' 'below explicit threshold'
Pass 'Get-Verdict never passes without every required success.'

$line = '2026-11-07T10:11:12.1234567+02:00 Warning [taskbar] message 0xC155 not decoded'
$parsed = ConvertFrom-HostLogLine $line
Assert-Equal $parsed.Level 'Warning' 'log level'
Assert-Equal $parsed.Area 'taskbar' 'log area'
Assert-Equal $parsed.Message 'message 0xC155 not decoded' 'log message'
Assert-Equal $parsed.When.UtcDateTime.ToString('HH:mm:ss') '08:11:12' 'log time converted'
Assert-Equal (ConvertFrom-HostLogLine '2026-11-07T10:11:12Z Info [host] ready').Area 'host' 'Z suffix'
Assert-True ($null -eq (ConvertFrom-HostLogLine 'not a log line')) 'garbage is rejected'
Assert-True ($null -eq (ConvertFrom-HostLogLine '2026-11-07T10:11:12Z Debug [x] y')) 'unknown level is rejected'
Pass 'ConvertFrom-HostLogLine parses the host log format and rejects the rest.'

Assert-Equal (Get-NextPhase 'new' $false) 'coexistence' 'new'
Assert-Equal (Get-NextPhase 'coexistence' $false) 'done' 'coexistence only'
Assert-Equal (Get-NextPhase 'coexistence' $true) 'replacement-arm' 'coexistence then replacement'
Assert-Equal (Get-NextPhase 'replacement-arm' $true) 'replacement-run' 'arm'
Assert-Equal (Get-NextPhase 'replacement-run' $true) 'restore-check' 'run always goes to the restore check'
Assert-Equal (Get-NextPhase 'restore-check' $true) 'done' 'restore check'
Assert-Equal (Get-NextPhase 'done' $true) 'done' 'done is final'
try { Get-NextPhase 'bogus' $true | Out-Null; throw 'no exception' } catch { Assert-True ($_.Exception.Message -like "Unknown phase*") 'unknown phase throws' }
Pass 'Get-NextPhase always routes the replacement run through the restore check.'

Assert-Equal (ConvertTo-RunnerResult 'Pass') 'Pass' 'Pass'
Assert-Equal (ConvertTo-RunnerResult 'Fail') 'Fail' 'Fail'
Assert-Equal (ConvertTo-RunnerResult 'Inconclusive') 'Blocked' 'Inconclusive is never a Pass'
Assert-Equal (ConvertTo-RunnerResult 'Skipped') 'NotRun' 'Skipped'
Pass 'ConvertTo-RunnerResult never turns an unobserved check into a Pass.'

Assert-Equal (Get-GateVerdict @('Pass', 'Pass')) 'Pass' 'all pass'
Assert-Equal (Get-GateVerdict @('Pass', 'Fail', 'Inconclusive')) 'Fail' 'any fail'
Assert-Equal (Get-GateVerdict @('Pass', 'Inconclusive')) 'Inconclusive' 'a pass beside an inconclusive'
Assert-Equal (Get-GateVerdict @('Pass', 'Skipped')) 'Inconclusive' 'a skipped check keeps the gate from passing'
Assert-Equal (Get-GateVerdict @('Skipped', 'Skipped')) 'Skipped' 'all skipped'
Assert-Equal (Get-GateVerdict @()) 'Skipped' 'empty'
Pass 'Get-GateVerdict passes a gate only when every check passed.'

Assert-Equal (Get-RunnerTest 'Coexistence') 'Coexistence' 'Coexistence'
Assert-Equal (Get-RunnerTest 'S28') 'WinX' 'S28'
Assert-Equal (Get-RunnerTest 'S29.2') 'Hover' 'S29.2'
Assert-Equal (Get-RunnerTest 'S31') 'AltTab' 'S31'
Assert-Equal (Get-RunnerTest 'S30') 'Desktop' 'S30'
Assert-Equal (Get-RunnerTest 'ExplorerRestore') 'ExplorerRestore' 'ExplorerRestore'
Assert-Equal (Get-RunnerTest 'Replacement') 'ReplacementSignIn' 'Replacement'
Assert-Equal (Get-RunnerTest 'WaysBack') 'ReplacementSignIn' 'WaysBack'
Assert-Equal (Get-RunnerTest 'S28 (replacement)') 'Other' 'replacement-shell reruns stay separate'
Assert-Equal (Get-RunnerTest 'S29.4') 'Other' 'S29.4'
$runnerSource = [IO.File]::ReadAllText((Join-Path $tools 'Test-AcceptanceVM.ps1'))
foreach ($name in 'Coexistence', 'WinX', 'Hover', 'AltTab', 'Desktop', 'ExplorerRestore', 'ReplacementSignIn', 'Other') {
    Assert-True ($runnerSource.Contains("'$name'")) "the manual runner accepts -Test $name"
}
Pass 'every scripted gate maps onto a -Test value the manual runner accepts.'

$still = @(Get-StillManual)
foreach ($needle in 'S24.1', 'S27.4', 'S29.3', 'S29.4', 'S30 / S31', '150 %', 'password', 'week of daily use') {
    Assert-True (@($still | Where-Object { $_ -like "*$needle*" }).Count -ge 1) "still-manual list mentions $needle"
}
Pass 'the still-manual list names every item a script cannot settle.'

$probeLog = @(
    '2026-11-07T10:00:00.000Z qpc=1 run=0 TaskbarButtonCreated-received hwnd=0x1',
    '2026-11-07T10:00:00.100Z qpc=2 run=1 sequence-start',
    '2026-11-07T10:00:00.200Z qpc=3 run=1 call-begin SetProgressState',
    '2026-11-07T10:00:00.210Z qpc=4 run=1 SetProgressState hr=0x00000000',
    '2026-11-07T10:00:02.210Z qpc=5 run=1 SetOverlayIcon hr=0x00000000',
    '2026-11-07T10:00:22.000Z qpc=6 run=1 sequence-end',
    '2026-11-07T10:00:23.000Z qpc=7 run=1 cleanup-begin',
    '2026-11-07T10:00:23.500Z qpc=8 run=1 end readinessReceived=1 runs=1',
    'garbage line'
)
$probe = Get-TaskbarProbeFacts -Lines $probeLog
Assert-Equal $probe.Lines 8 'probe lines parsed'
Assert-True $probe.Readiness 'probe readiness'
Assert-Equal $probe.NativeCalls 2 'probe native calls'
Assert-Equal $probe.FailedCalls 0 'probe failed calls'
Assert-Equal $probe.SequencesEnded 1 'probe sequences ended'
Assert-True $probe.CleanEnd 'probe clean end'
Assert-Equal $probe.Runs 1 'probe runs'
$failed = Get-TaskbarProbeFacts -Lines ($probeLog + '2026-11-07T10:00:03.000Z qpc=9 run=1 SetOverlayIcon hr=0x80004005')
Assert-Equal $failed.FailedCalls 1 'a failing HRESULT is counted'
Assert-Equal @($failed.FailedDetail).Count 1 'a failing HRESULT is described'
$empty = Get-TaskbarProbeFacts -Lines @()
Assert-Equal $empty.Lines 0 'no probe lines'
Assert-True (-not $empty.Readiness) 'no readiness without lines'
Pass 'Get-TaskbarProbeFacts counts what the probe log states.'

$traceLines = @(
    '{"type":"start","utc":"2026-11-07T10:00:00Z"}',
    '{"type":"message","message":"0xC155","hwnd":1}',
    '{"type":"message","message":"0xC155","hwnd":1}',
    '{"type":"message","message":"0xC156","hwnd":1}',
    'not json',
    '{"type":"end","eligible":3,"captured":3,"dropped":0}'
)
$trace = Get-TraceFacts -Lines $traceLines
Assert-True $trace.Started 'trace started'
Assert-True $trace.Ended 'trace ended'
Assert-Equal $trace.Messages 3 'trace messages'
Assert-Equal $trace.Unparsable 1 'trace unparsable'
Assert-Equal $trace.Eligible 3 'trace eligible'
Assert-Equal @($trace.Distinct).Count 2 'trace distinct messages'
$live = Get-TraceFacts -Lines $traceLines[0..2]
Assert-True ($live.Started -and -not $live.Ended) 'a live trace has no end record'
Pass 'Get-TraceFacts reads the host trace, live or finished.'

$baseline = [pscustomobject]@{ Readiness = $true; NativeCalls = 2; FailedCalls = 0; SequencesEnded = 1 }
$ok = Get-TaskbarTransportVerdict -Probe $probe -Trace $live -TraceFailedInLog $false -Baseline $baseline
Assert-Equal $ok.Verdict 'Inconclusive' 'the transport verdict is never a Pass on its own'
Assert-True ($ok.Reason -match 'match the Explorer baseline') 'a matching baseline is stated'
$mismatch = Get-TaskbarTransportVerdict -Probe $probe -Trace $live -TraceFailedInLog $false -Baseline ([pscustomobject]@{ Readiness = $true; NativeCalls = 10; FailedCalls = 0; SequencesEnded = 1 })
Assert-True ($mismatch.Reason -match 'differs from the Explorer baseline in native call count') 'a baseline mismatch is named'
Assert-True ((Get-TaskbarTransportVerdict -Probe $probe -Trace $null -TraceFailedInLog $false -Baseline $baseline).Reason -match 'no host trace file') 'a missing trace is named'
Assert-True ((Get-TaskbarTransportVerdict -Probe $probe -Trace $live -TraceFailedInLog $true -Baseline $baseline).Reason -match 'TaskbarTraceFailed') 'a failed trace start is named'
Assert-True ((Get-TaskbarTransportVerdict -Probe $probe -Trace $live -TraceFailedInLog $false -Baseline $null).Reason -match 'no Explorer baseline') 'a missing baseline is named'
Assert-True ((Get-TaskbarTransportVerdict -Probe $empty -Trace $live -TraceFailedInLog $false -Baseline $baseline).Reason -match 'no log') 'an empty probe log is named'
Pass 'Get-TaskbarTransportVerdict never passes and names every reason.'

$wins = @(
    [pscustomobject]@{ Handle = 10; Visible = $true }, [pscustomobject]@{ Handle = 20; Visible = $true },
    [pscustomobject]@{ Handle = 99; Visible = $true }, [pscustomobject]@{ Handle = 30; Visible = $true }
)
Assert-Equal (Get-PanelZState -Windows $wins -Panels @(10, 20) -Fixture 99) 'above' 'panels above the fixture'
Assert-Equal (Get-PanelZState -Windows $wins -Panels @(30) -Fixture 99) 'below' 'panel below the fixture'
Assert-Equal (Get-PanelZState -Windows $wins -Panels @(10, 30) -Fixture 99) 'mixed' 'panels on both sides'
Assert-Equal (Get-PanelZState -Windows $wins -Panels @(10) -Fixture 5) 'unknown' 'fixture missing'
Assert-Equal (Get-PanelZState -Windows $wins -Panels @(77) -Fixture 99) 'unknown' 'panel missing'
$hidden = @([pscustomobject]@{ Handle = 10; Visible = $false }, [pscustomobject]@{ Handle = 99; Visible = $true })
Assert-Equal (Get-PanelZState -Windows $hidden -Panels @(10) -Fixture 99) 'hidden' 'panels hidden'
Pass 'Get-PanelZState classifies the panel z-order against a fixture window.'

$settingsText = "{`n  `"theme`": `"dark`",`n  `"activePreset`" : `"subzerodev.default`",`n  `"x`": 1`n}"
$switched = Set-PresetInSettingsText -Text $settingsText -PresetId 'subzerodev.sample-retro'
Assert-True ($switched -match '"activePreset" : "subzerodev.sample-retro"') 'the preset is replaced in place'
Assert-True ($switched -match '"theme": "dark"') 'the rest of the settings is untouched'
Assert-Equal ($switched.Length - $settingsText.Length) ('subzerodev.sample-retro'.Length - 'subzerodev.default'.Length) 'only the preset text changed'
Assert-True ($null -eq (Set-PresetInSettingsText -Text '{"a":1}' -PresetId 'x')) 'no activePreset means no edit'
Pass 'Set-PresetInSettingsText swaps only the activePreset string.'

$records = @(
    [pscustomobject]@{ gate = 'S28'; check = 'winx-real-mouse-x20'; verdict = 'Fail'; detail = '12 of 20'; stage = 'coexistence'; evidence = '' },
    [pscustomobject]@{ gate = 'S31'; check = 'commit'; verdict = 'Pass'; detail = '10 of 10'; stage = 'coexistence'; evidence = '' },
    [pscustomobject]@{ gate = 'S31'; check = 'live-thumbnails'; verdict = 'Inconclusive'; detail = 'look'; stage = 'coexistence'; evidence = '' }
)
$report = Get-AcceptanceReport -Records $records -StillManual @('item one') -Facts @{ Machine = 'Test VM' }
Assert-True ($report -match '\| S28 \| Fail \|') 'failed gate shown as Fail'
Assert-True ($report -match '\| S31 \| Inconclusive \|') 'a gate with an inconclusive check is not shown as Pass'
Assert-True ($report -match 'Still manual') 'report lists the manual items'
Assert-True ($report -match 'Machine: Test VM') 'report lists the facts'
Assert-True ($report -notmatch '\| S31 \| Pass \|') 'no false pass'
Pass 'Get-AcceptanceReport reports honest per-gate verdicts and the manual items.'

Write-Host 'All scripted-acceptance checks passed.'
