# Shared helpers for the scripted VM acceptance suite. Dot-source; do not run directly.
# Windows PowerShell 5.1 compatible (the guest's default): no ?? / ?: / && and C# 5 only.
# Everything here observes the real desktop through non-invasive channels (window
# enumeration, UI Automation, the host log, screenshots) and drives it with real
# SendInput. The test hook (SUBZERODEV_WM_TEST_HOOK) must NOT be set.

$script:AccUtf8 = New-Object Text.UTF8Encoding($false)

. (Join-Path $PSScriptRoot 'Logic.ps1')

# ---------------------------------------------------------------- guard

function Assert-DisposableVm {
    param([switch] $Acknowledged)
    if ($env:OS -ne 'Windows_NT') { throw 'Run this inside the Windows acceptance VM.' }
    if ($env:SUBZERODEV_WM_TEST_HOOK) { throw 'SUBZERODEV_WM_TEST_HOOK is set. The real-desktop suite must run without the test hook.' }
    $system = Get-CimInstance Win32_ComputerSystem
    if (-not (Test-VmIdentity $system.Manufacturer $system.Model ([bool]$system.HypervisorPresent))) {
        throw ('Refusing to run: this does not look like a virtual machine (' + $system.Manufacturer + ' / ' + $system.Model +
            ', hypervisor=' + $system.HypervisorPresent + '). The suite sends real input and can register the shell.')
    }
    if (-not $Acknowledged) { throw 'Pass -ConfirmDisposableVm to confirm this VM has a snapshot you can restore and nothing in it matters.' }
    return $system
}

# ---------------------------------------------------------------- native layer

if (-not ('WmAcc' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class WmWindowInfo
{
    public long Handle; public uint ProcessId; public uint ThreadId; public string Title; public string ClassName;
    public int Left, Top, Right, Bottom; public bool Visible; public bool Minimized;
}

public static class WmAcc
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO
    {
        public int cbSize; public uint flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT
    {
        public int length; public int flags; public int showCmd; public int ptMinX, ptMinY, ptMaxX, ptMaxY; public RECT rcNormal;
    }
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const uint InputMouse = 0, InputKeyboard = 1;
    const uint KeyUp = 2, KeyExtended = 1, KeyScan = 8;
    const uint MouseMove = 0x1, MouseLeftDown = 0x2, MouseLeftUp = 0x4, MouseRightDown = 0x8, MouseRightUp = 0x10, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000, MouseWheel = 0x800;

    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch (Exception) { } }
    public static uint SystemDpi() { try { return GetDpiForSystem(); } catch (Exception) { return 96; } }

    public static void Key(ushort vk, bool up, bool extended)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = InputKeyboard;
        input[0].u.ki.wVk = vk;
        input[0].u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        input[0].u.ki.dwFlags = (up ? KeyUp : 0) | (extended ? KeyExtended : 0);
        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    static void Mouse(uint flags, int x, int y, uint data)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = InputMouse;
        input[0].u.mi.dx = x; input[0].u.mi.dy = y; input[0].u.mi.mouseData = data; input[0].u.mi.dwFlags = flags;
        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        int nx = (int)(((long)(x - left) * 65535L) / Math.Max(1, width - 1));
        int ny = (int)(((long)(y - top) * 65535L) / Math.Max(1, height - 1));
        Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, nx, ny, 0);
    }

    public static void ButtonDown(bool right) { Mouse(right ? MouseRightDown : MouseLeftDown, 0, 0, 0); }
    public static void ButtonUp(bool right) { Mouse(right ? MouseRightUp : MouseLeftUp, 0, 0, 0); }
    public static void Wheel(int notches) { Mouse(MouseWheel, 0, 0, (uint)(notches * 120)); }

    public static int[] Cursor() { POINT p; GetCursorPos(out p); return new int[] { p.X, p.Y }; }
    public static int[] Screen() { return new int[] { GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79), GetSystemMetrics(80) }; }

    public static long Foreground() { return GetForegroundWindow().ToInt64(); }

    // GUITHREADINFO.flags: 0x1 caret, 0x2 move/size, 0x4 menu mode, 0x8 system-menu mode, 0x10 popup-menu mode.
    public static uint GuiFlags(uint thread)
    {
        GUITHREADINFO info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        return GetGUIThreadInfo(thread, ref info) ? info.flags : 0xFFFFFFFF;
    }

    public static void Close(long handle) { PostMessage(new IntPtr(handle), 0x0010, IntPtr.Zero, IntPtr.Zero); }
    public static void Restore(long handle) { ShowWindow(new IntPtr(handle), 9); }

    public static List<WmWindowInfo> Windows()
    {
        List<WmWindowInfo> found = new List<WmWindowInfo>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            WmWindowInfo w = new WmWindowInfo();
            w.Handle = h.ToInt64();
            w.Visible = IsWindowVisible(h);
            w.Minimized = IsIconic(h);
            StringBuilder title = new StringBuilder(256); GetWindowText(h, title, 256); w.Title = title.ToString();
            StringBuilder cls = new StringBuilder(256); GetClassName(h, cls, 256); w.ClassName = cls.ToString();
            uint pid; w.ThreadId = GetWindowThreadProcessId(h, out pid); w.ProcessId = pid;
            RECT r; if (GetWindowRect(h, out r)) { w.Left = r.Left; w.Top = r.Top; w.Right = r.Right; w.Bottom = r.Bottom; }
            found.Add(w);
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}
[WmAcc]::MakeDpiAware()
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes

# ---------------------------------------------------------------- context

$script:Acc = $null

function Initialize-Acceptance {
    param([Parameter(Mandatory)][string] $Install, [Parameter(Mandatory)][string] $Results, [string] $Stage = 'adhoc')
    $dataRoot = if ($env:SUBZERODEV_WM_DATA_ROOT) { $env:SUBZERODEV_WM_DATA_ROOT } else { Join-Path $env:LOCALAPPDATA 'SubZeroDev\WindowManager' }
    [IO.Directory]::CreateDirectory($Results) | Out-Null
    $script:Acc = [pscustomobject]@{
        Install = $Install; Results = $Results; Stage = $Stage; DataRoot = $dataRoot; GateSuffix = ''
        Exe = (Join-Path $Install 'SubZeroDev.WindowManager.exe')
        Records = New-Object Collections.ArrayList
        Fixtures = New-Object Collections.ArrayList
    }
    return $script:Acc
}

function Get-Stamp { [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') }

function Add-Observation {
    param([string] $Gate, [string] $Check, [ValidateSet('Pass','Fail','Inconclusive','Skipped')][string] $Verdict, [string] $Detail = '', [string[]] $Evidence = @())
    $record = [pscustomobject][ordered]@{
        utc = [DateTime]::UtcNow.ToString('o'); stage = $script:Acc.Stage; gate = ($Gate + $script:Acc.GateSuffix); check = $Check
        verdict = $Verdict; detail = $Detail; evidence = ($Evidence -join ';')
    }
    [void]$script:Acc.Records.Add($record)
    $csv = Join-Path $script:Acc.Results 'auto-observations.csv'
    $record | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8 -Append
    $colour = switch ($Verdict) { 'Pass' { 'Green' } 'Fail' { 'Red' } 'Inconclusive' { 'Yellow' } default { 'Gray' } }
    Write-Host ('[{0}] {1} / {2}: {3}' -f $Verdict.ToUpperInvariant(), $record.gate, $Check, $Detail) -ForegroundColor $colour
}

# ---------------------------------------------------------------- waiting, windows, process

function Wait-Until {
    param([scriptblock] $Condition, [int] $TimeoutMs = 5000, [int] $PollMs = 50)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds $PollMs
    } while ($clock.ElapsedMilliseconds -lt $TimeoutMs)
    return $null
}

function Get-TopWindows {
    param([uint32[]] $ProcessId, [string] $TitleLike, [string] $ClassName, [switch] $IncludeHidden)
    $all = [WmAcc]::Windows()
    foreach ($w in $all) {
        if (-not $IncludeHidden -and -not $w.Visible) { continue }
        if ($ProcessId -and ($ProcessId -notcontains $w.ProcessId)) { continue }
        if ($TitleLike -and $w.Title -notlike $TitleLike) { continue }
        if ($ClassName -and $w.ClassName -ne $ClassName) { continue }
        $w
    }
}

function Get-HostProcessIds {
    @(Get-Process -Name 'SubZeroDev.WindowManager.Host' -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
}

function Get-ForegroundInfo {
    $handle = [WmAcc]::Foreground()
    $match = [WmAcc]::Windows() | Where-Object { $_.Handle -eq $handle } | Select-Object -First 1
    if ($match) { return $match }
    return [pscustomobject]@{ Handle = $handle; ProcessId = 0; ThreadId = 0; Title = ''; ClassName = ''; Left = 0; Top = 0; Right = 0; Bottom = 0 }
}

function Stop-Tree {
    param([int[]] $ProcessId)
    foreach ($id in $ProcessId) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- input

$script:Vk = @{ ALT = 0xA4; LALT = 0xA4; CTRL = 0xA2; SHIFT = 0xA0; WIN = 0x5B; TAB = 0x09; ESC = 0x1B; ENTER = 0x0D; DOWN = 0x28; UP = 0x26; RIGHT = 0x27; LEFT = 0x25; F2 = 0x71; F5 = 0x74; F6 = 0x75; DELETE = 0x2E; SPACE = 0x20; APPS = 0x5D }
function ConvertTo-KeyCode([string] $Name) {
    if ($script:Vk.ContainsKey($Name.ToUpperInvariant())) { return [uint16]$script:Vk[$Name.ToUpperInvariant()] }
    if ($Name.Length -eq 1) { return [uint16][char]$Name.ToUpperInvariant() }
    throw "Unknown key '$Name'"
}
function Test-Extended([uint16] $Code) { return @(0x5B, 0x5D, 0x28, 0x26, 0x27, 0x25, 0x2E) -contains $Code }

function Send-KeyDown([string] $Name) { $c = ConvertTo-KeyCode $Name; [WmAcc]::Key($c, $false, (Test-Extended $c)) }
function Send-KeyUp([string] $Name) { $c = ConvertTo-KeyCode $Name; [WmAcc]::Key($c, $true, (Test-Extended $c)) }
function Send-KeyTap([string] $Name, [int] $HoldMs = 30) { Send-KeyDown $Name; Start-Sleep -Milliseconds $HoldMs; Send-KeyUp $Name }

# Presses the modifiers, taps the key, releases in reverse order: a real chord, with no marker.
function Send-Chord {
    param([string[]] $Modifiers, [string] $Key, [int] $GapMs = 40)
    foreach ($m in $Modifiers) { Send-KeyDown $m; Start-Sleep -Milliseconds $GapMs }
    Send-KeyTap $Key
    Start-Sleep -Milliseconds $GapMs
    for ($i = $Modifiers.Count - 1; $i -ge 0; $i--) { Send-KeyUp $Modifiers[$i]; Start-Sleep -Milliseconds $GapMs }
}

function Send-Text([string] $Text) { [Windows.Forms.SendKeys]::SendWait(($Text -replace '([+^%~(){}\[\]])', '{$1}')) }

function Move-MouseTo { param([int] $X, [int] $Y, [int] $Steps = 6)
    $from = [WmAcc]::Cursor()
    for ($i = 1; $i -le $Steps; $i++) {
        [WmAcc]::MoveTo([int]($from[0] + ($X - $from[0]) * $i / $Steps), [int]($from[1] + ($Y - $from[1]) * $i / $Steps))
        Start-Sleep -Milliseconds 8
    }
}
function Click-At { param([int] $X, [int] $Y, [switch] $Right, [int] $HoldMs = 45)
    Move-MouseTo $X $Y
    Start-Sleep -Milliseconds 60
    [WmAcc]::ButtonDown([bool]$Right); Start-Sleep -Milliseconds $HoldMs; [WmAcc]::ButtonUp([bool]$Right)
}
function Get-Center($Rect) { [pscustomobject]@{ X = [int](($Rect.Left + $Rect.Right) / 2); Y = [int](($Rect.Top + $Rect.Bottom) / 2) } }

# ---------------------------------------------------------------- UI Automation

function Get-UiaItems {
    param([long] $Handle, [int] $Max = 200)
    $items = @()
    try {
        $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
        $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt [Math]::Min($all.Count, $Max); $i++) {
            $e = $all.Item($i)
            $r = $e.Current.BoundingRectangle
            if ($r.IsEmpty -or [double]::IsInfinity($r.Width)) { continue }
            $items += [pscustomobject]@{
                Name = $e.Current.Name; Type = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
                AutomationId = $e.Current.AutomationId; Enabled = $e.Current.IsEnabled
                Left = [int]$r.Left; Top = [int]$r.Top; Right = [int]$r.Right; Bottom = [int]$r.Bottom
            }
        }
    }
    catch { return @() }
    return $items
}

function Find-UiaItem { param([long] $Handle, [string] $NameLike)
    Get-UiaItems -Handle $Handle | Where-Object { $_.Name -like $NameLike -and $_.Right -gt $_.Left } | Select-Object -First 1
}

# ---------------------------------------------------------------- evidence

function Save-Screen {
    param([string] $Name)
    $bounds = [Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object Drawing.Bitmap $bounds.Width, $bounds.Height
    try {
        $g = [Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $g.Dispose()
        $path = Join-Path $script:Acc.Results ('{0}-{1}-{2}.png' -f $script:Acc.Stage, $Name, (Get-Stamp))
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        return $path
    }
    finally { $bitmap.Dispose() }
}

# Distinct colours among a grid of sample points inside a rectangle: a blank placeholder has very few.
function Get-RegionColourCount {
    param([int] $Left, [int] $Top, [int] $Right, [int] $Bottom, [int] $Grid = 24)
    $width = [Math]::Max(1, $Right - $Left); $height = [Math]::Max(1, $Bottom - $Top)
    $bitmap = New-Object Drawing.Bitmap $width, $height
    try {
        $g = [Drawing.Graphics]::FromImage($bitmap); $g.CopyFromScreen($Left, $Top, 0, 0, $bitmap.Size); $g.Dispose()
        $seen = @{}
        for ($x = 0; $x -lt $Grid; $x++) { for ($y = 0; $y -lt $Grid; $y++) {
            $c = $bitmap.GetPixel([int]($x * ($width - 1) / ($Grid - 1)), [int]($y * ($height - 1) / ($Grid - 1)))
            $seen[(($c.R -shr 3) * 1024 + ($c.G -shr 3) * 32 + ($c.B -shr 3))] = $true
        } }
        return $seen.Count
    }
    finally { $bitmap.Dispose() }
}

function Save-UiaDump { param([long] $Handle, [string] $Name)
    $path = Join-Path $script:Acc.Results ('{0}-{1}-{2}-uia.json' -f $script:Acc.Stage, $Name, (Get-Stamp))
    [IO.File]::WriteAllText($path, ((Get-UiaItems -Handle $Handle -Max 400) | ConvertTo-Json -Depth 4), $script:AccUtf8)
    return $path
}

function Save-WindowDump { param([string] $Name)
    $path = Join-Path $script:Acc.Results ('{0}-{1}-{2}-windows.json' -f $script:Acc.Stage, $Name, (Get-Stamp))
    $rows = [WmAcc]::Windows() | Where-Object { $_.Visible } | ForEach-Object {
        [pscustomobject]@{ handle = ('0x{0:X}' -f $_.Handle); pid = $_.ProcessId; title = $_.Title; class = $_.ClassName; left = $_.Left; top = $_.Top; right = $_.Right; bottom = $_.Bottom }
    }
    [IO.File]::WriteAllText($path, ($rows | ConvertTo-Json -Depth 3), $script:AccUtf8)
    return $path
}

# ---------------------------------------------------------------- host log

function Get-HostLogLines {
    param([DateTimeOffset] $Since = [DateTimeOffset]::MinValue)
    $directory = Join-Path $script:Acc.DataRoot 'logs'
    if (-not (Test-Path -LiteralPath $directory)) { return @() }
    $files = Get-ChildItem -LiteralPath $directory -Filter 'host-*.log' | Sort-Object Name | Select-Object -Last 2
    foreach ($file in $files) {
        $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $reader = New-Object IO.StreamReader($stream)
            while ($null -ne ($line = $reader.ReadLine())) {
                $entry = ConvertFrom-HostLogLine $line
                if ($entry -and $entry.When -ge $Since) { $entry }
            }
        }
        finally { $stream.Dispose() }
    }
}

function Get-HostProblems {
    param([DateTimeOffset] $Since)
    @(Get-HostLogLines -Since $Since | Where-Object { $_.Level -ne 'Info' })
}

# ---------------------------------------------------------------- the host under test

function Get-ShellRegistration {
    $key = Get-Item 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue
    if ($key) { return $key.GetValue('Shell', $null) }
    return $null
}

function Start-WindowManager {
    param([int] $ReadyTimeoutMs = 60000)
    $since = [DateTimeOffset]::UtcNow
    $guardian = @(Get-Process -Name 'SubZeroDev.WindowManager' -ErrorAction SilentlyContinue)
    if (-not $guardian.Count) {
        $p = Start-Process -FilePath $script:Acc.Exe -WorkingDirectory $script:Acc.Install -PassThru -WindowStyle Hidden
        if ($p.WaitForExit(4000)) { return [pscustomobject]@{ Ready = $false; Reason = ('guardian exited with ' + $p.ExitCode); Since = $since } }
    }
    $window = Wait-Until -TimeoutMs $ReadyTimeoutMs -PollMs 250 -Condition {
        $ids = Get-HostProcessIds
        if ($ids.Count) { Get-TopWindows -ProcessId $ids | Where-Object { $_.Title -like 'SubZeroDev.WindowManager*' } | Select-Object -First 1 }
    }
    if (-not $window) { return [pscustomobject]@{ Ready = $false; Reason = "no host window within $ReadyTimeoutMs ms"; Since = $since } }
    return [pscustomobject]@{ Ready = $true; Reason = ''; Since = $since }
}

function Stop-WindowManager {
    Get-Process -Name 'SubZeroDev.WindowManager', 'SubZeroDev.WindowManager.Host' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [void](Wait-Until -TimeoutMs 8000 -Condition { -not (Get-Process -Name 'SubZeroDev.WindowManager', 'SubZeroDev.WindowManager.Host' -ErrorAction SilentlyContinue) })
}

# ---------------------------------------------------------------- fixture windows (plain Win32 apps)

# A WinForms window in its own process (Windows PowerShell 5.1: the classic native HMENU menu bar, so Alt puts it in real menu mode).
function Start-FixtureWindow {
    param([Parameter(Mandatory)][string] $Title, [string] $Colour = 'LightSteelBlue', [int] $X = 120, [int] $Y = 120, [switch] $FullScreen)
    $fullScreenLines = if ($FullScreen) { '$f.FormBorderStyle = ''None''; $f.StartPosition = ''Manual''; $f.Bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds; $f.Menu = $null' } else { '' }
    $script = @"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
`$f = New-Object Windows.Forms.Form
`$f.Text = '$Title'; `$f.Width = 520; `$f.Height = 340; `$f.StartPosition = 'Manual'; `$f.Location = New-Object Drawing.Point($X, $Y)
`$f.BackColor = [Drawing.Color]::$Colour
`$mm = New-Object Windows.Forms.MainMenu
[void]`$mm.MenuItems.Add('&File'); [void]`$mm.MenuItems.Add('&Edit')
`$f.Menu = `$mm
$fullScreenLines
`$l = New-Object Windows.Forms.Label; `$l.Text = '$Title'; `$l.Font = New-Object Drawing.Font('Segoe UI', 28); `$l.AutoSize = `$true; `$l.Location = New-Object Drawing.Point(20, 80); `$f.Controls.Add(`$l)
[Windows.Forms.Application]::Run(`$f)
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-EncodedCommand', $encoded) -PassThru -WindowStyle Hidden
    [void]$script:Acc.Fixtures.Add($process)
    $window = Wait-Until -TimeoutMs 15000 -PollMs 100 -Condition { Get-TopWindows -TitleLike $Title | Select-Object -First 1 }
    if (-not $window) { throw "Fixture window '$Title' did not appear." }
    return $window
}

function Stop-Fixtures {
    foreach ($p in @($script:Acc.Fixtures)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $script:Acc.Fixtures.Clear()
}

# Brings a window to the foreground the way a user does: a click on its title bar.
function Set-ForegroundByClick {
    param($Window)
    if ([WmAcc]::Foreground() -eq $Window.Handle) { return $true }
    Click-At -X ([int](($Window.Left + $Window.Right) / 2)) -Y ($Window.Top + 14)
    return [bool](Wait-Until -TimeoutMs 2000 -PollMs 40 -Condition { [WmAcc]::Foreground() -eq $Window.Handle })
}
