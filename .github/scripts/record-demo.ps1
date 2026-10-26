<#
  record-demo.ps1
  Records the desktop to an MP4 (ffmpeg + gdigrab) while launching ASD.exe and clicking through
  its buttons with REAL mouse clicks (so the cursor and the clicks are visible in the video).
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
    [double] $PauseScale = 0.45,   # 1 = slow (old speed), 0.45 = fast. Scales every wait after a click
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

function Move-Mouse([int]$x, [int]$y, [int]$steps = 18) {
    $p = New-Object Native+POINT
    [void][Native]::GetCursorPos([ref]$p)
    $sx = $p.X; $sy = $p.Y
    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps
        $t = $t * $t * (3 - 2 * $t)                      # smooth ease-in/out
        [void][Native]::SetCursorPos([int]($sx + ($x - $sx) * $t), [int]($sy + ($y - $sy) * $t))
        Start-Sleep -Milliseconds 10
    }
}

function Click-Here {
    [Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down
    Start-Sleep -Milliseconds 80
    [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
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
    Start-Sleep -Milliseconds 250       # short hover dwell (hover animation still visible)
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

    # 1) pane slide animation: open, close, open again (stays open so the labels are visible)
    $toggle = Find-PaneToggle
    if ($toggle) {
        Click-Toggle $toggle 1500    # open
        Click-Toggle $toggle 1500    # close
        Click-Toggle $toggle 1800    # open
    } else { Log 'pane toggle button not found (see the title-bar button list above)' }

    # 2) main navigation
    [void](Click-Named 'Home' 1500)
    [void](Click-Named 'Video Player' 2200)

    # 3) Toolbox: open a few tools and come back each time
    [void](Click-Named 'Toolbox' 1800)
    foreach ($tool in 'Encode / Decode', 'Hash / HMAC', 'JSON Toolkit', 'Regex Tester') {
        if (Click-Named $tool 2200) {
            [void](Click-Named 'Toolbox' 1500)
        }
    }

    # 4) Generators: scroll the Toolbox if needed, then press "Generate" a few times
    if (Click-Named 'Generators' 1800 -scrollTries 3) {
        1..3 | ForEach-Object { [void](Click-Named 'Generate' 1400) }
        [void](Click-Named 'Toolbox' 1200)
    }

    # 5) Settings: switch theme Dark -> Light -> Follow Windows
    [void](Click-Named 'Settings' 1500)
    [void](Click-Named 'Dark' 2000)
    [void](Click-Named 'Follow Windows' 2000)          # = the runner's Windows theme
    $endName = switch ($AppTheme) { 'light' { 'Light' } 'dark' { 'Dark' } default { 'Follow Windows' } }
    [void](Click-Named $endName 1500)                  # finish on the theme the video started with

    # 6) Updates: opens its own window, wait for the result, then close that window
    if (Click-Named 'Updates' 1000) {
        $result = Wait-AnyName @('ASD is up to date', 'Update Failed', 'Update Failed - CD/R or directory is not writable') 40
        Log "update window result: $result"
        Start-Sleep -Milliseconds 2500                    # let the result be seen
        [void](Click-Named 'id:CloseButton' 1200)         # the XAML "Close" button (not the title-bar X)
    }

    # 7) back to Home for the last frame
    [void](Click-Named 'Home' 2000)
    if ($toggle) { Click-Toggle $toggle 1800 }   # close the pane once more
    Log 'scenario finished'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
}
finally {
    Stop-Recording
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
