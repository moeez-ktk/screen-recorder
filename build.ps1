<#
  Builds Light Recorder.

  There is no SDK to install, no package manager, no toolchain download: this
  compiles with the C# compiler that ships inside Windows itself. That is a
  deliberate constraint - the whole point of the app is to cost as little as
  possible, and a build that needs six gigabytes of tooling to produce a
  four-megabyte process is a strange way to start.

  Output: build\LightRecorder.exe

  Usage:
    .\build.ps1              # build
    .\build.ps1 -Run         # build, then start it
    .\build.ps1 -Dev         # unoptimised, with a console for stack traces
#>
[CmdletBinding()]
param(
  [switch]$Run,
  [switch]$Dev,
  [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---------------------------------------------------------------- compiler

$csc = $null
foreach ($candidate in @(
  'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
  'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)) {
  if (Test-Path $candidate) { $csc = $candidate; break }
}
if (-not $csc) {
  throw 'csc.exe not found. .NET Framework 4.x is required, and it ships with Windows 10 and 11.'
}

# ------------------------------------------------------------------ inputs

$sources = Get-ChildItem -Path (Join-Path $root 'app') -Filter '*.cs' -Recurse |
           Sort-Object FullName |
           ForEach-Object { $_.FullName }

if (-not $sources) { throw "No sources found under $root\app" }

$outDir = Join-Path $root 'build'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir 'LightRecorder.exe'

$icon = Join-Path $root 'assets\icon.ico'

# ----------------------------------------------------------------- compile

# /target:winexe keeps a console from flashing up when the app is launched at
# logon or from a hotkey. -Dev swaps to a console build so exceptions and
# Console.Error land somewhere visible.
$cscArgs = @(
  '/nologo'
  $(if ($Dev) { '/target:exe' } else { '/target:winexe' })
  '/platform:x64'
  '/unsafe+'
  '/langversion:5'
  "/out:$out"
  '/reference:System.dll'
  '/reference:System.Core.dll'
  '/reference:System.Drawing.dll'
  '/reference:System.Windows.Forms.dll'
)

if ($Dev) {
  $cscArgs += @('/optimize-', '/debug+', '/define:DEBUGCONSOLE')
} else {
  $cscArgs += @('/optimize+', '/debug-')
}

if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
$cscArgs += $sources

if (-not $Quiet) { Write-Host "Compiling $($sources.Count) files with $csc" -ForegroundColor DarkGray }

# A running copy holds a lock on the exe. It is resident by design, so this is
# the normal state of affairs rather than an unusual one.
$running = Get-Process LightRecorder -ErrorAction SilentlyContinue
if ($running) {
  if (-not $Quiet) { Write-Host 'Stopping the running instance first.' -ForegroundColor DarkGray }
  $running | Stop-Process -Force
  Start-Sleep -Milliseconds 400
}

# csc writes its "only supports C# 5" advisory to stdout on every run; it is
# not a warning about this code, so it is filtered rather than shown.
& $csc @cscArgs 2>&1 |
  Where-Object { $_ -notmatch 'only supports language versions up to C# 5' -and $_ -notmatch '^\s*$' -and $_ -notmatch 'This compiler is provided as part' } |
  ForEach-Object { Write-Host $_ }

if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

# The manifest asks for per-monitor DPI awareness and long path support. It is
# written next to the exe rather than embedded because csc cannot embed one
# without the SDK's mt.exe.
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="LightRecorder" version="2.0.0.0" processorArchitecture="amd64" />
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">permonitorv2,permonitor</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and 11. Without these the OS lies about its version. -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}" />
    </application>
  </compatibility>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
'@
Set-Content -Path (Join-Path $outDir 'LightRecorder.exe.manifest') -Value $manifest -Encoding UTF8

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
if (-not $Quiet) { Write-Host "Built $out ($size KB)" -ForegroundColor Green }

# ---------------------------------------------------------------- listener
#
# The hotkey listener is the one process that stays resident, so it is its own
# tiny executable: no WinForms, no Drawing, one source file.

$listenerSrc = Join-Path $root 'listener\HotkeyListener.cs'
$listenerOut = Join-Path $outDir 'LightRecorderHotkey.exe'
if (Test-Path $listenerSrc) {
  $runningListener = Get-Process LightRecorderHotkey -ErrorAction SilentlyContinue
  if ($runningListener) { $runningListener | Stop-Process -Force; Start-Sleep -Milliseconds 300 }

  $listenerArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/langversion:5'
    "/out:$listenerOut"
    '/reference:System.dll'
  )
  if (Test-Path $icon) { $listenerArgs += "/win32icon:$icon" }
  $listenerArgs += $listenerSrc

  & $csc @listenerArgs 2>&1 |
    Where-Object { $_ -notmatch 'only supports language versions up to C# 5' -and $_ -notmatch '^\s*$' -and $_ -notmatch 'This compiler is provided as part' } |
    ForEach-Object { Write-Host $_ }
  if ($LASTEXITCODE -ne 0) { throw "Listener compilation failed with exit code $LASTEXITCODE" }

  $lsize = [math]::Round((Get-Item $listenerOut).Length / 1KB, 1)
  if (-not $Quiet) { Write-Host "Built $listenerOut ($lsize KB)" -ForegroundColor Green }

  # It was resident before the build stopped it, so put it back.
  if ($runningListener) { Start-Process -FilePath $listenerOut }
}

if ($Run) {
  Get-Process LightRecorder -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 300
  Start-Process -FilePath $out
  Write-Host 'Started.' -ForegroundColor Green
}
