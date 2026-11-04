<#
  Runs every file in .github/automation-tests with the real ASD.exe (no window: --run / --check) and checks
  the exit code and the log. Safe on any computer: it only runs harmless commands (echo, exit, ping 127.0.0.1)
  and keeps ASD's data in a temporary folder (--data-dir).

  Output: one "OK:" or "PROBLEM:" line per check, and the log of every test that has a problem.
  The script's own exit code is 1 if any check failed.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$TestDir = (Join-Path $PSScriptRoot '..\automation-tests'),
    [string]$LogDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'asd-automation-tests')
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$dataDir = Join-Path $LogDir 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# Name, extra switches, expected exit code, lines the log must match (regex), lines it must NOT match.
$tests = @(
    @{ File = '01-success-chain.automation'; Mode = '--run'; Exit = 0
       Must = @('DONE\s+a\s+exit 0', 'hello-from-a', 'DONE\s+b\s+exit 0', 'SKIP\s+never', 'DONE\s+done\s+exit 0', 'RESULT OK')
       Not = @('RUN\s+never', '\$ echo SHOULD-NOT-RUN') },
    @{ File = '02-failure-handled.automation'; Mode = '--run'; Exit = 0
       Must = @('FAIL\s+a\s+exit 3', 'handled-exit-3', 'SKIP\s+never', 'handled by an arrow', 'RESULT OK')
       Not = @('RUN\s+never') },
    @{ File = '03-unhandled-failure.automation'; Mode = '--run'; Exit = 1
       Must = @('FAIL\s+a\s+exit 1', 'SKIP\s+b', 'RESULT FAILED\s+\(not handled: a\)')
       Not = @('RUN\s+b') },
    @{ File = '04-when-command.automation'; Mode = '--run'; Exit = 0
       Must = @('yes-ran', 'SKIP\s+no\b', 'RESULT OK')
       Not = @('RUN\s+no\b') },
    @{ File = '05-variables.automation'; Mode = '--run'; Exit = 0
       Must = @('got-token-123-code-0', 'notify \[success\] result 0', 'RESULT OK')
       Not = @() },
    @{ File = '06-output-in-command.automation'; Mode = '--run'; Exit = 1
       Must = @('not allowed inside a command', 'FAIL\s+b', 'RESULT FAILED')
       Not = @() },
    @{ File = '07-http-failure.automation'; Mode = '--run'; Exit = 0
       Must = @('FAIL\s+a', 'http-failed-handled', 'RESULT OK')
       Not = @() },
    @{ File = '08-join.automation'; Mode = '--run'; Exit = 0
       Must = @('merged', 'SKIP\s+strict', 'RESULT OK')
       Not = @('RUN\s+strict') },
    @{ File = '09-loop.automation'; Mode = '--check'; Exit = 2
       Must = @('loop', 'INVALID')
       Not = @() },
    @{ File = '10-from-here.automation'; Mode = '--run'; Exit = 0; Extra = @('--from', 'b')
       Must = @('ran-b', 'ran-c', 'RESULT OK')
       Not = @('ran-a', 'RUN\s+a\b') },
    @{ File = '11-parse-errors.automation'; Mode = '--check'; Exit = 2
       Must = @('line 3: unknown setting "colour"', 'line 4: cell "a" points to "ghost"', 'INVALID')
       Not = @() },
    @{ File = '12-timeout.automation'; Mode = '--run'; Exit = 0
       Must = @('took longer than 2 s', 'timed-out-handled', 'RESULT OK')
       Not = @() },
    # own arrows: exit code, text in the output, a command, all together; and an own color / name
    @{ File = '13-custom-arrows.automation'; Mode = '--run'; Exit = 0
       Must = @('ran-two', 'ran-found', 'ran-combo', 'SKIP\s+three', 'SKIP\s+missing', 'SKIP\s+never', 'RESULT OK')
       Not = @('RUN\s+three', 'RUN\s+missing', 'RUN\s+never', 'SHOULD-NOT-RUN') },
    @{ File = '14-arrow-errors.automation'; Mode = '--check'; Exit = 2
       Must = @('line 4: "notacolor" is not a color', 'line 5: "exit" needs a number', 'line 6: unknown arrow option "size"', 'line 7: "contains" needs a text', 'INVALID')
       Not = @() },
    # a status cell takes the result of the cell before it; "otherwise" is the else branch
    @{ File = '15-status-cell.automation'; Mode = '--run'; Exit = 0
       Must = @('status: a ended with exit 3', 'FAIL\s+check\s+exit 3', 'ran-three', 'SKIP\s+zero', 'SKIP\s+other', 'RESULT OK')
       Not = @('RUN\s+zero', 'RUN\s+other', 'SHOULD-NOT-RUN') },
    @{ File = '16-otherwise.automation'; Mode = '--run'; Exit = 0
       Must = @('ran-rest', 'SKIP\s+one', 'SKIP\s+two', 'RESULT OK')
       Not = @('RUN\s+one', 'RUN\s+two', 'SHOULD-NOT-RUN') },
    # upload cells: wrong settings are found when the file is read; a missing file is an ordinary failure that an arrow can handle
    @{ File = '17-upload-errors.automation'; Mode = '--check'; Exit = 2
       Must = @('has no "to:"', 'must be\s+dropbox', 'the post URL must start with', 'INVALID')
       Not = @() },
    @{ File = '18-upload-missing-file.automation'; Mode = '--run'; Exit = 0
       Must = @('The file does not exist', 'upload-failed-handled', 'RESULT OK')
       Not = @() },
    # "Run this cell" runs one cell and follows no arrow
    @{ File = '01-success-chain.automation'; Mode = '--run'; Exit = 0; Tag = 'only-a'; Extra = @('--only', 'a')
       Must = @('DONE\s+a\s+exit 0', 'hello-from-a', 'RESULT OK')
       Not = @('RUN\s+b\b', '->') },
    # the editor writes files with AutomationWriter: writing and reading again must change nothing
    @{ File = '01-success-chain.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '02-failure-handled.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '04-when-command.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '05-variables.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '07-http-failure.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '08-join.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '10-from-here.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '13-custom-arrows.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '15-status-cell.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '16-otherwise.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '18-upload-missing-file.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    @{ File = '12-timeout.automation'; Mode = '--roundtrip'; Exit = 0; Tag = 'roundtrip'; Must = @('ROUNDTRIP OK'); Not = @() },
    # a valid file is accepted by --check
    @{ File = '01-success-chain.automation'; Mode = '--check'; Exit = 0; Tag = 'check'
       Must = @('VALID', '4 cells')
       Not = @() }
)

$bad = 0
foreach ($t in $tests) {
    $name = $t.File
    if ($t.Tag) { $name = "$name ($($t.Tag))" }
    $file = Join-Path $TestDir $t.File
    $log = Join-Path $LogDir (([IO.Path]::GetFileNameWithoutExtension($t.File)) + $(if ($t.Tag) { "-$($t.Tag)" } else { '' }) + '.log')
    if (Test-Path $log) { Remove-Item $log -Force }

    $argList = @('--github-actions', '--data-dir', ('"' + $dataDir + '"'), $t.Mode, ('"' + $file + '"'), '--log', ('"' + $log + '"'))
    if ($t.Extra) { $argList += $t.Extra }

    $problems = New-Object System.Collections.Generic.List[string]
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $ExePath -ArgumentList $argList -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(120000)) {
            try { $p.Kill() } catch { }
            [void]$problems.Add('did not finish within 120 s')
        }
        else {
            if ($p.ExitCode -ne $t.Exit) { [void]$problems.Add("exit code was $($p.ExitCode), expected $($t.Exit)") }
        }
        $sw.Stop()

        $text = ''
        if (Test-Path $log) { $text = [System.IO.File]::ReadAllText($log) } else { [void]$problems.Add('no log file was written') }
        foreach ($m in $t.Must) { if ($text -notmatch $m) { [void]$problems.Add("log is missing: $m") } }
        foreach ($m in $t.Not) { if ($text -match $m) { [void]$problems.Add("log must not contain: $m") } }
    }
    catch {
        [void]$problems.Add("script error: $($_.Exception.Message)")
    }

    if ($problems.Count -eq 0) {
        Write-Host "OK: $name  ($([int]$sw.Elapsed.TotalSeconds) s)"
    }
    else {
        $bad++
        foreach ($pr in $problems) { Write-Host "PROBLEM: $name - $pr" }
        if (Test-Path $log) {
            Write-Host "----- log of $name -----"
            Get-Content $log | ForEach-Object { Write-Host "  $_" }
            Write-Host '-----'
        }
    }
}

Write-Host ''
if ($bad -eq 0) { Write-Host "ALL $($tests.Count) AUTOMATION CHECKS PASSED" }
else { Write-Host "$bad of $($tests.Count) AUTOMATION CHECKS FAILED" }
exit $(if ($bad -eq 0) { 0 } else { 1 })
