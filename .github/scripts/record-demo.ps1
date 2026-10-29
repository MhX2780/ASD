<#
  record-demo.ps1
  Records the desktop to an MP4 (ffmpeg + gdigrab) while launching ASD.exe and clicking
  Scenario 'notifications' (default): download + notifications only. Scenario 'keys': ONLY through the Keys Store page (create the vault, add keys, favorite, quick copy, search/filter, open, edit, export a backup, delete keys, add a key, import the backup, lock/unlock, then the notification bell with a local test server) with REAL mouse clicks (so the cursor and the clicks are visible in the video).
  Elements are located with Windows UI Automation (by their visible text), never by fixed pixels.

  Usage (from the workflow):
    pwsh -File .github/scripts/record-demo.ps1 -ExePath <ASD.exe> -OutFile <demo.mp4> -LogFile <log.txt>

  The script never fails the build: if a button is not found it logs the names it DID see
  (so the next run can be fixed quickly) and carries on.
#>
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [string] $OutFile,
    [string] $LogFile = (Join-Path $env:RUNNER_TEMP 'record-demo-log.txt'),
    [int] $Fps = 60,   # the runner display is 60 Hz, so more than 60 adds nothing
    [ValidateSet('notifications','keys','all')] [string] $Scenario = 'notifications',   # notifications = bell + local test server only; keys = Keys Store tour; all = both
    [double] $PauseScale = 0.4,   # 1 = slow (old speed), 0.4 = fast. Scales every wait after a click
    [string] $UpdateInfoUrl = 'https://github.com/MhX2780/ASD/releases/download/latest/update.txt',   # same URL the app checks
    [ValidateSet('light','dark','auto')] [string] $AppTheme = 'light',   # the app's own theme setting at start (the video starts in this theme)
    [ValidateSet('light','dark')] [string] $SystemTheme = 'light'   # what "Follow Windows" should look like
)

$ErrorActionPreference = 'Continue'
Set-Content -Path $LogFile -Value "record-demo started $(Get-Date -Format o)"

function Log([string]$m) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

# ------------------------- Win32 + UI Automation -------------------------
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, ref int pvParam, uint winIni);
}
"@
[void][Native]::SetProcessDPIAware()   # real pixels: UIA rectangles and SetCursorPos must agree

# The GitHub Windows runner is DARK by default, so "Follow Windows" in the app looks dark.
# Set the Windows theme (apps + taskbar) before anything starts, so "Follow Windows" = $SystemTheme.
function Set-SystemTheme([string]$mode) {
    $v = if ($mode -eq 'light') { 1 } else { 0 }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name AppsUseLightTheme -Value $v -Type DWord
    Set-ItemProperty -Path $key -Name SystemUsesLightTheme -Value $v -Type DWord
    $res = [UIntPtr]::Zero
    [void][Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'ImmersiveColorSet', 2, 2000, [ref]$res)   # WM_SETTINGCHANGE
    Log "Windows theme set to $mode"
}

# CI sessions often run with "animation effects" OFF, which is why WinUI animations (pane slide,
# selection indicator, hover fades) do not appear in the video. Turn them on for this session.
function Enable-Animations {
    $before = 0
    [void][Native]::SystemParametersInfo(0x1042, 0, [ref]$before, 0)           # SPI_GETCLIENTAREAANIMATION
    Log "client-area animations before: $before"
    [void][Native]::SystemParametersInfo(0x1043, 0, [IntPtr]1, 2)              # SPI_SETCLIENTAREAANIMATION = on
    [void][Native]::SystemParametersInfo(0x103F, 0, [IntPtr]1, 2)              # SPI_SETUIEFFECTS = on
    [void][Native]::SystemParametersInfo(0x1003, 0, [IntPtr]1, 2)              # SPI_SETMENUANIMATION = on
    $after = 0
    [void][Native]::SystemParametersInfo(0x1042, 0, [ref]$after, 0)
    Log "client-area animations after: $after"
}

$script:proc = $null
$script:recStart = Get-Date
$script:trimSec = 0

# every top-level window of the app (main window + the "ASD Update" window when it is open)
function Get-AppWindows {
    if (-not $script:proc) { return @() }
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:proc.Id)
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}

# the MAIN window (title "ASD"), never the update window
function Get-AppRoot {
    $wins = Get-AppWindows
    if ($wins.Count -eq 0) { return $null }
    $main = $wins | Where-Object { $_.Current.Name -eq 'ASD' } | Select-Object -First 1
    if ($main) { return $main }
    return $wins[0]
}

function Move-Mouse([int]$x, [int]$y, [int]$steps = 10) {
    $p = New-Object Native+POINT
    [void][Native]::GetCursorPos([ref]$p)
    $sx = $p.X; $sy = $p.Y
    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps
        $t = $t * $t * (3 - 2 * $t)                      # smooth ease-in/out
        [void][Native]::SetCursorPos([int]($sx + ($x - $sx) * $t), [int]($sy + ($y - $sy) * $t))
        Start-Sleep -Milliseconds 8
    }
}

function Click-Here {
    [Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down
    Start-Sleep -Milliseconds 50
    [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
}

function Scroll-Up([int]$notches = 3) {
    for ($i = 0; $i -lt $notches; $i++) {
        [Native]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)    # wheel up
        Start-Sleep -Milliseconds 120
    }
}

function Scroll-Down([int]$notches = 3) {
    for ($i = 0; $i -lt $notches; $i++) {
        [Native]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero)   # wheel down
        Start-Sleep -Milliseconds 120
    }
}

# Finds a visible element whose Name equals $name (prefers real buttons / list items over plain text).
# $name = visible text, or 'id:SomeName' to search by AutomationId (the x:Name from the XAML).
function Find-Element([string]$name) {
    if ($name.StartsWith('id:')) {
        $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $name.Substring(3))
    } else {
        $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    }
    $all = @()
    foreach ($w in (Get-AppWindows)) {
        $all += @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))   # filtered inside UIA = fast
    }
    $preferred = 'ControlType.Button', 'ControlType.ListItem', 'ControlType.RadioButton', 'ControlType.TabItem', 'ControlType.CheckBox'
    $hits = @()
    foreach ($el in $all) {
        try {
            $c = $el.Current
            if ($c.IsOffscreen) { continue }
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 2 -or $r.Height -lt 2) { continue }
            $rank = if ($preferred -contains $c.ControlType.ProgrammaticName) { 0 } else { 1 }
            $hits += [pscustomobject]@{ El = $el; Rank = $rank; Rect = $r }
        } catch { }
    }
    if ($hits.Count -eq 0) { return $null }
    return ($hits | Sort-Object Rank | Select-Object -First 1)
}

function Dump-Names([string]$why) {
    Log "  > not found ($why). Visible element names were:"
    $root = Get-AppRoot
    if (-not $root) { Log '    (no app window)'; return }
    $names = @()
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $c = $el.Current
            if ($c.Name -and -not $c.IsOffscreen) { $names += ("{0} [{1}]" -f $c.Name.Substring(0, [Math]::Min(60, $c.Name.Length)), $c.ControlType.ProgrammaticName.Replace('ControlType.', '')) }
        } catch { }
    }
    ($names | Select-Object -Unique -First 60) | ForEach-Object { Log "    $_" }
}

# Waits until one of the names appears (e.g. the result text of the update window). Returns the name or $null.
function Wait-AnyName([string[]]$names, [int]$timeoutSec = 30) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $end) {
        foreach ($n in $names) { if (Find-Element $n) { return $n } }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

# Moves the cursor to the element and clicks it. Returns $true on success.
function Click-Named([string]$name, [int]$pauseMs = 1800, [int]$scrollTries = 0) {
    $hit = Find-Element $name
    $attempt = 0
    while (-not $hit -and $attempt -lt $scrollTries) {
        $attempt++
        $b = (Get-AppRoot).Current.BoundingRectangle
        Move-Mouse ([int]($b.X + $b.Width * 0.6)) ([int]($b.Y + $b.Height * 0.6)) 12
        Scroll-Down 3
        Start-Sleep -Milliseconds 400
        $hit = Find-Element $name
    }
    if (-not $hit) { Log "CLICK '$name' -> NOT FOUND"; Dump-Names $name; return $false }
    $r = $hit.Rect
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    Move-Mouse $x $y
    Start-Sleep -Milliseconds 170       # short hover dwell (hover animation still visible)
    Click-Here
    Log "CLICK '$name' at ($x,$y)"
    Start-Sleep -Milliseconds ([int]($pauseMs * $PauseScale))
    return $true
}

# ------------------------- ffmpeg -------------------------
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { Log 'ffmpeg not found - the clicks will still run but no video will be produced.' }

$rawFile = Join-Path (Split-Path $OutFile -Parent) 'raw-capture.mp4'
Remove-Item $rawFile, $OutFile -Force -ErrorAction SilentlyContinue
$ff = $null; $ffErr = $null

function Start-Recording {
    if (-not $ffmpeg) { return }
    # fragmented MP4 while recording = still playable even if ffmpeg has to be killed
    $ffArgs = "-y -hide_banner -loglevel warning -f gdigrab -framerate $Fps -draw_mouse 1 -i desktop " +
              "-vf `"scale=trunc(iw/2)*2:trunc(ih/2)*2`" -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p " +
              "-movflags +frag_keyframe+empty_moov `"$rawFile`""
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ffmpeg
    $psi.Arguments = $ffArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardError = $true
    $script:ff = [System.Diagnostics.Process]::Start($psi)
    $script:ffErr = $script:ff.StandardError.ReadToEndAsync()   # drain so ffmpeg never blocks
    Log "ffmpeg recording started (pid $($script:ff.Id), ${Fps}fps, screen $([Native]::GetSystemMetrics(0))x$([Native]::GetSystemMetrics(1)))"
}

function Stop-Recording {
    if (-not $script:ff) { return }
    try {
        $script:ff.StandardInput.WriteLine('q')     # graceful stop: ffmpeg finalizes the file
        $script:ff.StandardInput.Flush()
    } catch { }
    if (-not $script:ff.WaitForExit(30000)) { Log 'ffmpeg did not stop in 30s - killing it'; $script:ff.Kill() }
    try { if ($script:ffErr.Wait(3000) -and $script:ffErr.Result.Trim()) { Log "ffmpeg: $($script:ffErr.Result.Trim())" } } catch { }
    Log "ffmpeg stopped (exit $($script:ff.ExitCode))"
}

# Finds the hamburger (pane toggle) button in the title bar and logs every button of the top strip.
function Find-PaneToggle {
    $root = Get-AppRoot
    if (-not $root) { return $null }
    $win = $root.Current.BoundingRectangle
    $btns = @()
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $c = $el.Current
            if ($c.ControlType.ProgrammaticName -ne 'ControlType.Button' -or $c.IsOffscreen) { continue }
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Y -gt ($win.Y + 70) -or $r.Width -lt 8) { continue }
            $btns += [pscustomobject]@{ Name = $c.Name; Id = $c.AutomationId; Rect = $r }
        } catch { }
    }
    foreach ($b in $btns) { Log ("  title-bar button: name='{0}' id='{1}' x={2:N0}" -f $b.Name, $b.Id, $b.Rect.X) }
    $byName = $btns | Where-Object { $_.Name -match 'pane|navigation|menu|toggle|hamburger' -or $_.Id -match 'Pane|Toggle|Navigation' } | Select-Object -First 1
    if ($byName) { return $byName }
    # fallback: the left-most button of the title bar (the back button is hidden at start)
    return ($btns | Where-Object { $_.Rect.X -lt ($win.X + 120) } | Sort-Object { $_.Rect.X } | Select-Object -First 1)
}

function Click-Toggle($t, [int]$pauseMs = 1500) {
    $r = $t.Rect
    Move-Mouse ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 250
    Click-Here
    Log "CLICK pane toggle '$($t.Name)'"
    Start-Sleep -Milliseconds ([int]($pauseMs * $PauseScale))
}

# ------------------------- typing helpers (Keys Store demo) -------------------------
Add-Type -AssemblyName System.Windows.Forms
$script:DemoPassword = 'Demo-Pass-2026'

# types text like a person: one character at a time into whatever has the focus
function Type-Text([string]$text, [int]$delayMs = 14) {
    foreach ($ch in $text.ToCharArray()) {
        $k = [string]$ch
        if ('+^%~(){}[]'.Contains($k)) { $k = '{' + $k + '}' }
        [System.Windows.Forms.SendKeys]::SendWait($k)
        Start-Sleep -Milliseconds $delayMs
    }
}
function Press-Keys([string]$keys, [int]$ms = 100) {
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds $ms
}

# Finds a visible TEXT FIELD (TextBox / PasswordBox) by its header text. Plain Find-Element would also match the
# header's own TextBlock, and clicking that does not focus the box.
function Find-Field([string]$name) {
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($w in (Get-AppWindows)) {
        foreach ($el in @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))) {
            try {
                $c = $el.Current
                if ($c.IsOffscreen) { continue }
                if ($c.ControlType.ProgrammaticName -ne 'ControlType.Edit' -and $c.ControlType.ProgrammaticName -ne 'ControlType.Document') { continue }
                $r = $c.BoundingRectangle
                if ($r.IsEmpty -or $r.Width -lt 2 -or $r.Height -lt 2) { continue }
                return [pscustomobject]@{ El = $el; Rect = $r }
            } catch { }
        }
    }
    return $null
}

# Clicks INTO a text field so it really has the keyboard focus, then (optionally) types. Never relies on Tab order.
function Fill-Field([string]$field, [string]$text) {
    $hit = Find-Field $field
    $tries = 0
    while (-not $hit -and $tries -lt 4) {
        # a dialog that is taller than the screen scrolls: its fields are "offscreen" until the wheel brings them in
        $tries++
        $b = (Get-AppRoot).Current.BoundingRectangle
        Move-Mouse ([int]($b.X + $b.Width / 2)) ([int]($b.Y + $b.Height / 2)) 8
        if ($tries -le 2) { Scroll-Down 3 } else { Scroll-Up 8 }
        Start-Sleep -Milliseconds 300
        $hit = Find-Field $field
    }
    if (-not $hit) {
        Log "FIELD '$field' -> NOT FOUND (typing into whatever has the focus)"
        Dump-Names $field
    } else {
        $r = $hit.Rect
        Move-Mouse ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) 8
        Start-Sleep -Milliseconds 100
        Click-Here
        try { $hit.El.SetFocus() } catch { }          # belt and braces: also focus through UI Automation
        Start-Sleep -Milliseconds 200
        Log "FIELD '$field' focused"
    }
    if ($text) { Type-Text $text }
}

# Chooses the key's icon in the dialog. $icon = a built-in name from the drop-down ('Cloud', 'Payments' ...)
# or 'more:<search>|<title>' to open "More icons", search for a brand logo and pick it (e.g. 'more:github|GitHub').
function Pick-Icon([string]$icon) {
    if (-not (Find-Element 'Icon picker')) {
        $b = (Get-AppRoot).Current.BoundingRectangle
        Move-Mouse ([int]($b.X + $b.Width / 2)) ([int]($b.Y + $b.Height / 2)) 8
        Scroll-Up 10
        Start-Sleep -Milliseconds 300
    }
    if (-not (Click-Named 'Icon picker' 500)) { return }
    if (-not $icon.StartsWith('more:')) { [void](Click-Named $icon 500); return }

    $spec  = $icon.Substring(5).Split('|')
    $query = $spec[0]; $title = $spec[1]
    if (-not (Click-Named 'More icons' 600)) { return }
    if (Wait-AnyName @('Search icons') 15) {
        Start-Sleep -Milliseconds 600
        Type-Text $query
        Start-Sleep -Milliseconds 1100
        [void](Click-Named ('Icon ' + $title) 700)           # closes "More icons", the Add dialog comes back with everything typed
        [void](Wait-AnyName @('Save') 15)
        Start-Sleep -Milliseconds 500
    }
}

# "Add new key" -> (first time: create the vault with the master password) -> fill the dialog -> pick an icon -> Save
function Add-Key([string]$name, [string]$service, [string]$tags, [string]$value, [string]$icon) {
    Log "adding key '$name'"
    if (-not (Click-Named 'Add new key' 700)) { return }
    $first = Wait-AnyName @('Create', 'Save') 30
    if ($first -eq 'Create') {                                   # the vault does not exist yet
        Start-Sleep -Milliseconds 500
        Fill-Field 'Master password (at least 8 characters)' $script:DemoPassword
        Fill-Field 'Repeat the password' $script:DemoPassword
        [void](Click-Named 'Create' 600)
        $first = Wait-AnyName @('Save') 30
    }
    if (-not $first) { Log "the add dialog did not appear for '$name'"; Dump-Names 'add dialog'; return }
    Start-Sleep -Milliseconds 500                                # let the dialog finish its opening animation
    Fill-Field 'Name' $name
    Fill-Field 'Service' $service
    Fill-Field 'Tags (comma separated)' $tags
    Fill-Field 'Value' $value
    if ($icon) { Pick-Icon $icon }
    [void](Click-Named 'Save' 900)

    # check the result: the dialog must be gone and the new card must be on the page
    Start-Sleep -Milliseconds 600
    if (Find-Element 'Save') {
        Log "PROBLEM: the add dialog is still open after Save for '$name' (a field was empty or invalid). Cancelling it so the next steps can run."
        Dump-Names "add dialog for $name"
        [void](Click-Named 'Cancel' 500)
        Start-Sleep -Milliseconds 400
    }
    if (Find-Element $name) { Log "OK: key '$name' is on the page" }
    else { Log "PROBLEM: key '$name' is not visible after Save"; Dump-Names "key $name" }
}

# opens Security and presses one of its backup buttons ('Export backup' / 'Import backup'); the dialog closes and the file picker opens
function Open-BackupPicker([string]$button) {
    if (-not (Click-Named 'Security' 600)) { return $false }
    if (-not (Wait-AnyName @($button) 10)) { Log "Security dialog: '$button' not found"; Dump-Names $button; return $false }
    Start-Sleep -Milliseconds 700
    [void](Click-Named $button 600)
    # the Windows file dialog is a window of the app: wait for its "File name:" box, then type the full path and press Enter
    if (-not (Wait-AnyName @('File name:') 25)) { Log 'the file dialog did not show a File name box (typing into whatever has the focus)'; Dump-Names 'file dialog'; Start-Sleep -Seconds 2 }
    Start-Sleep -Milliseconds 700
    return $true
}

# presses Delete for a key: open it, Edit, Delete, confirm
function Delete-Key([string]$name) {
    Log "deleting key '$name'"
    if (-not (Click-Named $name 500)) { return }
    if (Wait-AnyName @('Edit') 20) {
        [void](Click-Named 'Edit' 500)
        if (Wait-AnyName @('Save') 10) {
            Start-Sleep -Milliseconds 500
            if (Click-Named 'Delete' 500) {
                Start-Sleep -Milliseconds 900                 # the edit dialog closes, the confirm dialog opens
                [void](Click-Named 'Delete' 700)
            }
        }
    }
    Start-Sleep -Milliseconds 500
    if (Find-Element $name) { Log "PROBLEM: '$name' is still on the page" } else { Log "OK: '$name' is gone" }
}

# ------------------------- scenario -------------------------
try {
    Set-SystemTheme $SystemTheme
    Enable-Animations
    # Run a COPY of the build, so the update window can never touch the files that go into ASD.zip.
    # The copy gets the same update.txt as the latest release -> the Updates window ends with "ASD is up to date"
    # instead of downloading and replacing the app.
    $runDir = Join-Path (Split-Path $OutFile -Parent) 'asd-run'
    Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Split-Path $ExePath -Parent) -Destination $runDir -Recurse -Force
    $ExePath = Join-Path $runDir 'ASD.exe'
    try {
        Invoke-WebRequest -Uri $UpdateInfoUrl -OutFile (Join-Path $runDir 'update.txt') -UseBasicParsing -TimeoutSec 30
        Log 'copied the latest release update.txt next to the app (so Updates says "up to date")'
    } catch { Log "could not download update.txt ($($_.Exception.Message)) - the Updates window will show an error, nothing is installed" }

    # Seed the app's own theme setting, so the video starts light even though the runner's Windows is dark.
    $settingsDir = Join-Path $env:LOCALAPPDATA 'ASD'
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    $settingsFile = Join-Path $settingsDir 'settings.json'
    if ($AppTheme -eq 'auto') { Remove-Item $settingsFile -Force -ErrorAction SilentlyContinue }
    else { [System.IO.File]::WriteAllText($settingsFile, ('{"ThemeMode":"' + $AppTheme + '"}'), (New-Object System.Text.UTF8Encoding($false))) }
    Log "app theme setting: $AppTheme"

    # everything slow is done: NOW start recording (so the video does not open on the runner's console)
    Start-Recording
    $script:recStart = Get-Date
    Start-Sleep -Milliseconds 700

    $script:proc = Start-Process -FilePath $ExePath -PassThru
    Log "ASD started (pid $($script:proc.Id))"

    # wait for the main window to appear in UI Automation
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Get-AppRoot)) { Start-Sleep -Milliseconds 500 }
    if (-not (Get-AppRoot)) { Log 'main window never appeared - stopping'; throw 'no window' }

    $script:trimSec = [Math]::Max(0, ((Get-Date) - $script:recStart).TotalSeconds - 0.3)   # cut the console frames before the app appears
    Log ('video will start {0:N1}s into the capture' -f $script:trimSec)
    $script:proc.Refresh()
    if ($script:proc.MainWindowHandle -ne [IntPtr]::Zero) { [void][Native]::SetForegroundWindow($script:proc.MainWindowHandle) }

    Start-Sleep -Seconds 4          # splash (~1.4 s) + first render
    Move-Mouse 400 300 10

    # ===== Keys Store: open it, create the vault, add 4 keys, then try every feature =====
    # the pane may start closed (its items are then not on screen): open it with the hamburger button first
    if (-not (Find-Element 'Keys Store')) {
        $toggle = Find-PaneToggle
        if ($toggle) { Click-Toggle $toggle 1000 } else { Log 'pane toggle button not found (see the title-bar button list above)' }
    }
    if ($Scenario -ne 'notifications') {
    [void](Click-Named 'Keys Store' 900)

    # 1) four keys, each with its own icon (the first one also creates the vault + master password)
    Add-Key 'Azure-Storage'  'Azure'  'cloud, prod' 'DefaultEndpointsProtocol-demo-0001' 'Cloud'
    Add-Key 'GitHub-Token'   'GitHub' 'dev, ci'     'ghp-demo-0002-AbCdEfGh'              'more:github|GitHub'
    Add-Key 'Gemini-API'     'Google' 'ai, prod'    'AIza-demo-0003-XyZ'                  'more:gemini|Google Gemini'
    Add-Key 'Stripe-Live'    'Stripe' 'billing'     'sk-live-demo-0004-QwErTy'            'Payments'

    # 2) favorite: the star of the first card
    [void](Click-Named 'Add to favorites' 700)

    # 3) Search dialog: type, clear, filters, then pick a result (it opens the key dialog)
    if (Click-Named 'Search' 700) {
        if (Wait-AnyName @('Search keys') 10) {
            Start-Sleep -Milliseconds 500
            Type-Text 'git'                                  # the box is focused when the dialog opens
            Start-Sleep -Milliseconds 900
            Press-Keys '^a'; Press-Keys '{DELETE}' 500
            if (Click-Named 'All keys' 500) { [void](Click-Named 'Favorites' 900) }
            if (Click-Named 'All tags' 500) { [void](Click-Named 'prod' 900) }
            [void](Click-Named 'Result Azure-Storage' 600)   # closes the search and opens the key dialog
            if (Wait-AnyName @('Copy') 20) {
                [void](Click-Named 'Copy' 700)
                [void](Click-Named 'Hide' 600)
                [void](Click-Named 'Show' 600)
                [void](Click-Named 'Close' 500)
            }
        }
    }

    # 4) open a key from its card: information dialog -> Copy, Hide, Show, Close
    if (Click-Named 'GitHub-Token' 500) {
        if (Wait-AnyName @('Copy') 20) {
            [void](Click-Named 'Copy' 700)
            [void](Click-Named 'Hide' 600)
            [void](Click-Named 'Show' 600)
            [void](Click-Named 'Close' 500)
        }
    }

    # 5) open a key and press Edit in its dialog, then cancel
    if (Click-Named 'Gemini-API' 500) {
        if (Wait-AnyName @('Edit') 15) {
            [void](Click-Named 'Edit' 500)
            if (Wait-AnyName @('Save') 10) { Start-Sleep -Milliseconds 500; [void](Click-Named 'Cancel' 500) }
        }
    }

    # 6) rename the first card with its pencil
    if (Click-Named 'Edit or rename' 500) {
        if (Wait-AnyName @('Save') 10) {
            Start-Sleep -Milliseconds 500
            Fill-Field 'Name' ''
            Press-Keys '^a'; Type-Text 'Azure-Storage-Prod'
            [void](Click-Named 'Save' 700)
        }
    }

    try {
    # 7) BACKUP: export the vault to a file (Security -> Export backup -> file dialog)
    $backup = Join-Path $runDir 'demo-backup.asdvault'
    Remove-Item $backup -Force -ErrorAction SilentlyContinue
    if (Open-BackupPicker 'Export backup') {
        Type-Text $backup
        Press-Keys '{ENTER}' 1500
        if (Test-Path $backup) { Log ("OK: backup file written ({0} bytes)" -f (Get-Item $backup).Length) } else { Log 'PROBLEM: the backup file was not written' }
    }

    # 8) delete two keys, then add a new one
    Delete-Key 'GitHub-Token'
    Delete-Key 'Gemini-API'
    Add-Key 'Twilio-SMS' 'Twilio' 'sms, prod' 'SK-demo-0005-TwIlIo' 'Email'

    # 9) IMPORT the backup: the two deleted keys come back, the new key stays
    if (Test-Path $backup) {
        if (Open-BackupPicker 'Import backup') {
            Type-Text $backup
            Press-Keys '{ENTER}' 1200
            if (Wait-AnyName @('Import') 15) {
                Start-Sleep -Milliseconds 500
                Fill-Field 'Master password of the backup' $script:DemoPassword
                [void](Click-Named 'Import' 1500)
            } else { Log 'the import password dialog did not appear'; Dump-Names 'import dialog' }
            Start-Sleep -Milliseconds 800
            foreach ($k in 'GitHub-Token', 'Gemini-API', 'Twilio-SMS') {
                if (Find-Element $k) { Log "OK: '$k' is on the page after the import" } else { Log "PROBLEM: '$k' missing after the import" }
            }
        }
    } else { Log 'no backup file, so the import step is skipped' }
    } catch { Log "ERROR in the backup steps: $($_.Exception.Message)" }

    try {
    # 9b) IMPORT keys from pasted .env text (Security -> Import from .env or JSON)
    if (Click-Named 'Security' 600) {
        if (Wait-AnyName @('Import from .env or JSON') 10) {
            Start-Sleep -Milliseconds 700
            [void](Click-Named 'Import from .env or JSON' 700)
            if (Wait-AnyName @('Choose file...', 'Import') 15) {
                Start-Sleep -Milliseconds 500
                Fill-Field 'Paste .env lines or JSON' ''
                Type-Text 'DEMO_OPENAI_KEY=sk-demo-0006'; Press-Keys '{ENTER}' 200
                Type-Text '# a comment line is ignored'; Press-Keys '{ENTER}' 200
                Type-Text 'DEMO_SENDGRID_KEY="SG.demo-0007"'; Press-Keys '{ENTER}' 600
                [void](Click-Named 'Import' 1200)
                Start-Sleep -Milliseconds 800
                foreach ($k in 'DEMO_OPENAI_KEY', 'DEMO_SENDGRID_KEY') {
                    if (Find-Element $k) { Log "OK: '$k' was imported" } else { Log "PROBLEM: '$k' is missing after the .env import" }
                }
            } else { Log 'the .env import dialog did not appear'; Dump-Names 'env import' }
        }
    }
    } catch { Log "ERROR in the .env import step: $($_.Exception.Message)" }

    # 10) lock from Security, then open a key again: the unlock dialog asks for the master password
    if (Click-Named 'Security' 600) {
        if (Wait-AnyName @('Lock now') 10) {
            Start-Sleep -Milliseconds 500
            [void](Click-Named 'Lock now' 700)
        }
    }
    if (Click-Named 'Stripe-Live' 500) {
        if (Wait-AnyName @('Unlock') 15) {
            Start-Sleep -Milliseconds 300
            Type-Text $script:DemoPassword
            [void](Click-Named 'Unlock' 500)
            if (Wait-AnyName @('Close') 20) { Start-Sleep -Milliseconds 800; [void](Click-Named 'Close' 500) }
        }
    }

    # the vault is open again: the quick Copy button on a card copies the key without opening it
    [void](Click-Named 'Copy key' 700)

    # last look at the Security dialog (Windows Hello, auto-lock, backup export / import), then close it
    if (Click-Named 'Security' 600) {
        Start-Sleep -Milliseconds 1200
        [void](Click-Named 'Close' 500)
    }

    try {
    }   # end of the Keys Store tour

    if ($Scenario -ne 'keys') {
    # Whatever happened before: close any dialog that is still open, so this test always starts from a clean page.
    Press-Keys '{ESC}' 400; Press-Keys '{ESC}' 400

    # 11) NOTIFICATIONS: the bell, its dialog, the cards, and the Windows notification when ASD is not the active window.
    #     A tiny local web server (a background job) gives a request and a download that each take about 4 seconds.
    Log 'notifications test: starting the local test server'
    $port = 18765
    $notesLog = Join-Path $env:LOCALAPPDATA 'ASD\notifications.log'
    Remove-Item $notesLog -Force -ErrorAction SilentlyContinue
    $script:serverJob = Start-Job -ArgumentList $port -ScriptBlock {
        param($port)
        $l = New-Object System.Net.HttpListener
        $l.Prefixes.Add("http://localhost:$port/")
        $l.Start()
        while ($l.IsListening) {
            $ctx = $l.GetContext()
            try {
                $path = $ctx.Request.Url.AbsolutePath
                if ($path -eq '/delay') {
                    Start-Sleep -Seconds 4
                    $bytes = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                    $ctx.Response.ContentType = 'application/json'
                    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                } elseif ($path -eq '/file') {
                    $chunk = New-Object byte[] 65536
                    $ctx.Response.ContentLength64 = 16 * 65536
                    $ctx.Response.ContentType = 'application/octet-stream'
                    $ctx.Response.AddHeader('Content-Disposition', 'attachment; filename="demo-download.bin"')
                    if ($ctx.Request.HttpMethod -ne 'HEAD') {
                        for ($i = 0; $i -lt 16; $i++) {
                            $ctx.Response.OutputStream.Write($chunk, 0, $chunk.Length)
                            $ctx.Response.OutputStream.Flush()
                            Start-Sleep -Milliseconds 300
                        }
                    }
                } elseif ($path -eq '/missing') {
                    Start-Sleep -Seconds 3                  # slow, so the person has left the page when it fails
                    $ctx.Response.StatusCode = 404
                } else {
                    $ctx.Response.StatusCode = 404
                }
            } catch { }
            finally { try { $ctx.Response.Close() } catch { } }
        }
    }
    Start-Sleep -Seconds 2

    function Wait-NotesLog([string]$text, [int]$timeoutSec) {
        $end = (Get-Date).AddSeconds($timeoutSec)
        while ((Get-Date) -lt $end) {
            if ((Test-Path $notesLog) -and (Select-String -Path $notesLog -Pattern $text -SimpleMatch -Quiet)) { return $true }
            Start-Sleep -Milliseconds 500
        }
        return $false
    }

    # a) a download that SUCCEEDS: start it from HTTP Download, then leave the page (Keys Store): the notification must still arrive
    if (Click-Named 'Toolbox' 900) {
        if (Click-Named 'HTTP Download' 900) {
            if (Wait-AnyName @('Download links') 10) {
                Fill-Field 'Download links' "http://localhost:$port/file"
                [void](Click-Named 'Download' 900)
                [void](Click-Named 'Keys Store' 700)                 # the person is on another page now
                if (Wait-NotesLog 'demo-download.bin' 90) { Log 'OK: the download notification was posted' } else { Log 'PROBLEM: no download notification after 90 s' }
                Start-Sleep -Milliseconds 800                         # the badge on the bell is visible now
                if (Click-Named 'Notifications' 800) {
                    if (Wait-AnyName @('demo-download.bin') 10) {
                        Start-Sleep -Milliseconds 1200               # let the list be seen
                        [void](Click-Named 'demo-download.bin' 1200)   # the card: goes to the tool
                        if (Wait-AnyName @('Download links') 10) { Log 'OK: the card opened HTTP Download' } else { Log 'PROBLEM: the card did not open HTTP Download' }
                    } else { Log 'PROBLEM: the card is not in the notifications dialog'; Dump-Names 'notifications' }
                }

                # b) a download that FAILS (the server answers 404): same way, ends in an error notification
                if (Wait-AnyName @('Download links') 5) {
                    Fill-Field 'Download links' ''
                    Press-Keys '^a'; Press-Keys '{DELETE}' 200
                    Type-Text "http://localhost:$port/missing"
                    [void](Click-Named 'Download' 900)
                    [void](Click-Named 'Keys Store' 700)
                    if (Wait-NotesLog 'Download failed' 60) { Log 'OK: the failed-download notification was posted' } else { Log 'PROBLEM: no failed-download notification after 60 s' }
                    if (Click-Named 'Notifications' 800) {
                        Start-Sleep -Milliseconds 1500
                        [void](Click-Named 'Close' 600)
                    }
                }
            } else { Log 'HTTP Download page did not open'; Dump-Names 'http download' }
        }
    }

    # c) HTTP Track: a request that takes 4 s while ASD is NOT the active window -> Windows notification
    if (Click-Named 'Toolbox' 900) {
        if (Click-Named 'HTTP Track' 900) {
            if (Wait-AnyName @('Send') 10) {
                Fill-Field 'Request URL' "http://localhost:$port/delay"
                [void](Click-Named 'Send' 500)
                $other = Start-Process notepad -PassThru           # takes the focus away from ASD
                Start-Sleep -Seconds 1
                try { [void][Native]::SetForegroundWindow($other.MainWindowHandle) } catch { }
                if (Wait-NotesLog 'localhost | HTTP Track' 40) { Log 'OK: the request notification was posted' } else { Log 'PROBLEM: no request notification after 40 s' }
                Start-Sleep -Seconds 1
                try { Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue } catch { }
                if ($script:proc.MainWindowHandle -ne [IntPtr]::Zero) { [void][Native]::SetForegroundWindow($script:proc.MainWindowHandle) }
                Start-Sleep -Milliseconds 800
                if (Click-Named 'Notifications' 800) {
                    if (Wait-AnyName @('GET localhost') 10) { Start-Sleep -Milliseconds 1200; Log 'OK: the request card is in the list' } else { Log 'PROBLEM: the request card is not in the list'; Dump-Names 'notifications' }
                    # d) Clear all empties the list
                    if (Click-Named 'Clear all' 800) {
                        if (Click-Named 'Notifications' 800) {
                            if (Wait-AnyName @('No notifications yet. Finished downloads and slow requests will appear here.') 5) { Log 'OK: Clear all emptied the list' } else { Log 'PROBLEM: the list was not empty after Clear all' }
                            Start-Sleep -Milliseconds 800
                            [void](Click-Named 'Close' 600)
                        }
                    } else { [void](Click-Named 'Close' 600) }
                }
            }
        }
    }

    # what the app itself wrote about the notifications (did Windows show them?)
    if (Test-Path $notesLog) { Get-Content $notesLog | ForEach-Object { Log "notifications.log: $_" } } else { Log 'PROBLEM: notifications.log does not exist' }
    } catch { Log "ERROR in the notifications test: $($_.Exception.Message)" }

    }   # end of the notifications test

    Log 'scenario finished'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
}
finally {
    Stop-Recording
    if ($script:serverJob) { Stop-Job $script:serverJob -ErrorAction SilentlyContinue; Remove-Job $script:serverJob -Force -ErrorAction SilentlyContinue }
    if ($script:proc -and -not $script:proc.HasExited) { Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name ASD -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue   # also a restarted copy
}

# ------------------------- finalize mp4 -------------------------
if ($ffmpeg -and (Test-Path $rawFile) -and (Get-Item $rawFile).Length -gt 1000) {
    # normal (non-fragmented) MP4 with the moov atom up front, smaller file
    & $ffmpeg -y -hide_banner -loglevel error -ss $($script:trimSec.ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)) -i $rawFile -c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p -movflags +faststart $OutFile 2>&1 | ForEach-Object { Log "ffmpeg(final): $_" }
    if (-not (Test-Path $OutFile) -or (Get-Item $OutFile).Length -lt 1000) {
        Log 'final encode failed - using the raw capture instead'
        Copy-Item $rawFile $OutFile -Force
    }
    Remove-Item $rawFile -Force -ErrorAction SilentlyContinue
    Log ("VIDEO READY: {0} ({1:N2} MB)" -f $OutFile, ((Get-Item $OutFile).Length / 1MB))
} else {
    Log 'no video produced'
}
exit 0
