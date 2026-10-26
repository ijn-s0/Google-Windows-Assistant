# UI test: rearrange pins (drag, context menu, Alt+Shift+Arrow) and remove suggestions (X button, Del key).
# Drives the real app with synthetic mouse/keyboard input and checks what was saved to settings.json.
param([string]$exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -Namespace T -Name M -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
'@
$UIA = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$results = New-Object System.Collections.Generic.List[string]
$failed = $false
function Check($name, $ok, $detail) {
  $line = ("{0}  {1}  {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail)
  Write-Host $line; $results.Add($line)
  if (-not $ok) { $script:failed = $true }
}
function Shot($name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save("shots/edit-$name.png"); $g.Dispose(); $bmp.Dispose()
}
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function MoveTo([int]$x, [int]$y) {
  [T.M]::SetCursorPos($x, $y) | Out-Null
  [T.M]::mouse_event(0x8001, [int]($x * 65535 / ($scr.Width - 1)), [int]($y * 65535 / ($scr.Height - 1)), 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 25
}
function Click([int]$x, [int]$y, [switch]$Right) {
  MoveTo $x $y; Start-Sleep -Milliseconds 250
  if ($Right) { [T.M]::mouse_event(0x08, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [T.M]::mouse_event(0x10, 0, 0, 0, [IntPtr]::Zero) }
  else { [T.M]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [T.M]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero) }
  Start-Sleep -Milliseconds 600
}
function Center($el) { $r = $el.Current.BoundingRectangle; return @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function FindNow($scope, $name) {
  $c = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $p.Id)),
    (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)))
  return $scope.FindFirst($TS::Descendants, $c)
}
function Find($scope, $name) {
  $c = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $p.Id)),
    (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)))
  for ($i = 0; $i -lt 20; $i++) { $e = $scope.FindFirst($TS::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  return $null
}
$dir = "$env:APPDATA\GeminiLauncher"
function Saved { Get-Content "$dir\settings.json" -Raw | ConvertFrom-Json }

# ---- seed: 4 pins, 4 recent searches, 1 recent file ----
New-Item -ItemType Directory -Force $dir | Out-Null
New-Item -ItemType Directory -Force C:\pt | Out-Null
Set-Content C:\pt\report-draft.txt "hello"
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut("$env:APPDATA\Microsoft\Windows\Recent\report-draft.txt.lnk"); $lnk.TargetPath = "C:\pt\report-draft.txt"; $lnk.Save()
@{
  firstRunDone = $true; defaultPinsDone = $true; showTrending = $false; takeOverWinKey = $false; takeOverStartButton = $false
  pins = @(
    @{ kind = 'url'; name = 'Pin One'; target = 'https://example.com/1' },
    @{ kind = 'url'; name = 'Pin Two'; target = 'https://example.com/2' },
    @{ kind = 'url'; name = 'Pin Three'; target = 'https://example.com/3' },
    @{ kind = 'url'; name = 'Pin Four'; target = 'https://example.com/4' })
  recentSearches = @('alpha test', 'beta test', 'gamma test', 'delta test')
} | ConvertTo-Json -Depth 5 | Set-Content "$dir\settings.json" -Encoding UTF8

$p = Start-Process $exe -ArgumentList @('--portable', '--show') -PassThru
Start-Sleep 7
$root = $UIA::RootElement
Shot "0-start"

# ---- 1. drag "Pin One" onto "Pin Three" ----
$one = Find $root 'Pin One'; $three = Find $root 'Pin Three'
if (-not $one -or -not $three) { Check 'find pins' $false 'tiles not found'; }
else {
  $a = Center $one; $b = Center $three
  MoveTo $a[0] $a[1]; Start-Sleep -Milliseconds 300
  [T.M]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 100
  for ($i = 1; $i -le 20; $i++) { MoveTo ([int]($a[0] + ($b[0] - $a[0]) * $i / 20)) ([int]($a[1] + ($b[1] - $a[1]) * $i / 20)); Start-Sleep -Milliseconds 15 }
  Start-Sleep -Milliseconds 300
  Shot "1-dragging"
  [T.M]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 800
  $order = (Saved).pins.name -join ', '
  Check 'drag pin to new position' ($order -eq 'Pin Two, Pin Three, Pin One, Pin Four') $order
  Check 'launcher still open after drag (no accidental click)' ($null -ne (Find $root 'Pin Two')) ''
  Shot "1-after-drag"
}

# ---- 2. right-click "Pin Four" -> Move to front ----
$four = Find $root 'Pin Four'
$c = Center $four
Click $c[0] $c[1] -Right
Shot "2-context-menu"
$mi = Find $root 'Move to front'
Check 'context menu opens with Move to front' ($null -ne $mi) ''
if ($mi) { $m = Center $mi; Click $m[0] $m[1] }
$order = (Saved).pins.name -join ', '
Check 'Move to front' ($order -eq 'Pin Four, Pin Two, Pin Three, Pin One') $order

# ---- 3. keyboard: Alt+Shift+Right on focused "Pin Four" ----
(Find $root 'Pin Four').SetFocus(); Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('%+{RIGHT}'); Start-Sleep -Milliseconds 600
$order = (Saved).pins.name -join ', '
Check 'Alt+Shift+Right moves pin' ($order -eq 'Pin Two, Pin Four, Pin Three, Pin One') $order
Shot "3-after-keyboard"

# ---- 4. hover a recent search and click its X ----
$row = Find $root 'alpha test'
$rc = Center $row
MoveTo $rc[0] $rc[1]; Start-Sleep -Milliseconds 500
$x = Find $row 'Remove from history'
Shot "4-hover-x"
Check 'X button on recent search' ($null -ne $x) ''
if ($x) { $xc = Center $x; Click $xc[0] $xc[1] }
$rs = (Saved).recentSearches -join ', '
Check 'X removes recent search' ($rs -eq 'beta test, gamma test, delta test') $rs
Check 'next search backfills the list' ($null -ne (Find $root 'delta test')) ''
Check 'launcher still open after X' ($null -ne (Find $root 'Pin Two')) ''

# ---- 5. Del key on a focused recent search ----
(Find $root 'beta test').SetFocus(); Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('{DEL}'); Start-Sleep -Milliseconds 600
$rs = (Saved).recentSearches -join ', '
Check 'Del removes focused recent search' ($rs -eq 'gamma test, delta test') $rs

# ---- 6. remove a Recommended file with its X ----
$doc = Find $root 'report-draft.txt'
Check 'recent file shown in Recommended' ($null -ne $doc) ''
if ($doc) {
  $dc = Center $doc; MoveTo $dc[0] $dc[1]; Start-Sleep -Milliseconds 500
  $dx = Find $doc 'Remove from list'
  Shot "6-hover-recommended"
  if ($dx) { $xc = Center $dx; Click $xc[0] $xc[1] }
  $hidden = (Saved).hiddenRecommended -join ', '
  Check 'X removes Recommended file' ($hidden -like '*report-draft.txt*') $hidden
  Start-Sleep 1
  $gone = Find $root 'report-draft.txt'
  Check 'removed file stays hidden' ($null -eq $gone -or $gone.Current.IsOffscreen) ''
}

# ---- 7. clicking outside closes the launcher ----
Click 6 ([int]($scr.Height / 2))
Start-Sleep 1
Shot "7-clicked-outside"
$still = FindNow $root 'Pin Two'
Check 'click outside closes the launcher' ($null -eq $still -or $still.Current.IsOffscreen) ''

# ---- 8. reopen: everything persisted ----
Stop-Process $p -Force; Start-Sleep 1
$p = Start-Process $exe -ArgumentList @('--portable', '--show') -PassThru
Start-Sleep 7
Shot "8-reopened"
$s = Saved
Check 'order persisted after restart' (($s.pins.name -join ', ') -eq 'Pin Two, Pin Four, Pin Three, Pin One') ($s.pins.name -join ', ')
Check 'removed file not shown after restart' ($null -eq (Find $root 'report-draft.txt')) ''
Stop-Process $p -Force -ErrorAction SilentlyContinue

# ---- 9. typing: apps come before Google results ----
$p = Start-Process $exe -ArgumentList @('--portable', '--query', 'microsoft') -PassThru
Start-Sleep 8
Shot "9-typing"
$web = Find $root 'Search the web'; $apps = FindNow $root 'Apps'
if ($web -and $apps) { Check 'apps listed above web results' ($apps.Current.BoundingRectangle.Y -lt $web.Current.BoundingRectangle.Y) '' }
else { Write-Host "SKIP  apps-above-web (only one matching app on this runner)"; $results.Add('SKIP  apps above web') }
Stop-Process $p -Force -ErrorAction SilentlyContinue

$results | Set-Content shots/edit-test.txt
$log = "$env:APPDATA\GeminiLauncher\error.log"
if (Test-Path $log) { Write-Host "---- error.log ----"; Get-Content $log }
if ($failed) { exit 1 }
