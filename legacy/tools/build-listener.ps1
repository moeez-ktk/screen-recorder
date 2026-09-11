<#
  Compiles the hotkey listener with the C# compiler that ships with Windows,
  so building it needs no SDK, no toolchain and no downloads.

  Output: tools\LightRecorderHotkey.exe
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $PSScriptRoot 'HotkeyListener.cs'
$out  = Join-Path $PSScriptRoot 'LightRecorderHotkey.exe'
$icon = Join-Path $root 'assets\icon.ico'

$csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' -ErrorAction SilentlyContinue
if (-not $csc) {
  $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' -ErrorAction SilentlyContinue
}
if (-not $csc) { throw 'csc.exe not found. .NET Framework 4.x is required (it ships with Windows).' }

# /target:winexe keeps a console window from flashing up on every hotkey.
$args = @(
  '/nologo'
  '/target:winexe'
  '/optimize+'
  '/platform:anycpu'
  "/out:$out"
  '/reference:System.dll'
  '/reference:System.Core.dll'
)
if (Test-Path $icon) { $args += "/win32icon:$icon" }
$args += $src

& $csc.FullName @args
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Built $out ($size KB)"

# Seed a config so the hotkeys work before the app has ever been launched.
# The app rewrites this on every start, so it stays in sync afterwards.
$confDir = Join-Path $env:APPDATA 'Light Recorder'
$conf    = Join-Path $confDir 'listener.conf'
if (-not (Test-Path $conf)) {
  New-Item -ItemType Directory -Force -Path $confDir | Out-Null
  $electron = Join-Path $root 'node_modules\electron\dist\electron.exe'
  if (Test-Path $electron) {
    @(
      '# Seeded by build-listener.ps1; the app rewrites this on launch.'
      "exec=$electron"
      "args=`"$root`""
      "cwd=$root"
      ''
      'toggleOverlay=Control+Shift+D'
      'startStop=Control+Alt+S'
      'stop=Control+Alt+X'
      'toggleMic=Control+Alt+M'
      'picker=Control+Alt+P'
    ) | Set-Content -Path $conf -Encoding UTF8
    Write-Host "Seeded $conf"
  } else {
    Write-Warning "Electron not found - run 'npm install', then 'npm start' once to generate $conf"
  }
}
