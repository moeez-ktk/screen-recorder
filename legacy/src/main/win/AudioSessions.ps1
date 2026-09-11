<#
  Windows Core Audio session control.

  Windows exposes a per-process audio session on the default render endpoint.
  That is the only supported way to mute one application without touching the
  rest of the system, so we drive IAudioSessionManager2 / ISimpleAudioVolume
  through COM interop directly.

  Usage:
    AudioSessions.ps1 -Action list
    AudioSessions.ps1 -Action mute   -ProcId 1234
    AudioSessions.ps1 -Action unmute -ProcId 1234
    AudioSessions.ps1 -Action volume -ProcId 1234 -Value 0.5
    AudioSessions.ps1 -Action serve      # read commands from stdin, one per line

  Compiling the interop below costs a couple of seconds, so the app runs this
  once in 'serve' mode and pipes commands to it rather than paying that price
  on every mixer refresh. Serve mode accepts:
    list | mute <pid> | unmute <pid> | volume <pid> <0..1> | quit
  and replies with exactly one line of JSON per command.

  Emits JSON on stdout. Exit code 0 on success.
#>
param(
  [Parameter(Mandatory = $true)][ValidateSet('list', 'mute', 'unmute', 'volume', 'serve')][string]$Action,
  [int]$ProcId = 0,
  [double]$Value = 1.0
)

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LightRec {

  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
  internal class MMDeviceEnumeratorComObject { }

  [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    int GetDevice(string id, out IMMDevice device);
    int RegisterEndpointNotificationCallback(IntPtr client);
    int UnregisterEndpointNotificationCallback(IntPtr client);
  }

  [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IMMDevice {
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IntPtr props);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
  }

  [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionManager2 {
    // --- IAudioSessionManager ---
    int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IAudioSessionControl session);
    int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
    // --- IAudioSessionManager2 ---
    int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    int RegisterSessionNotification(IntPtr notification);
    int UnregisterSessionNotification(IntPtr notification);
    int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
    int UnregisterDuckNotification(IntPtr duckNotification);
  }

  [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionEnumerator {
    int GetCount(out int count);
    int GetSession(int index, out IAudioSessionControl session);
  }

  [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionControl {
    int GetState(out int state);
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    int GetGroupingParam(out Guid groupingParam);
    int SetGroupingParam(ref Guid value, IntPtr eventContext);
    int RegisterAudioSessionNotification(IntPtr newNotifications);
    int UnregisterAudioSessionNotification(IntPtr newNotifications);
  }

  // Every method is explicitly [PreserveSig] so the raw HRESULT comes back
  // instead of the CLR turning it into an exception and re-shaping the
  // signature. IsSystemSoundsSession in particular returns S_FALSE (1) for a
  // normal app, which is a legitimate result rather than a failure.
  [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionControl2 {
    // --- IAudioSessionControl ---
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid value, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    // --- IAudioSessionControl2 ---
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
  }

  [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface ISimpleAudioVolume {
    int SetMasterVolume(float level, ref Guid eventContext);
    int GetMasterVolume(out float level);
    int SetMute(bool mute, ref Guid eventContext);
    int GetMute(out bool mute);
  }

  public class Session {
    public uint ProcessId;
    public string ProcessName;
    public string DisplayName;
    public bool Muted;
    public float Volume;
    public int State;          // 0 inactive, 1 active, 2 expired
    public bool SystemSounds;
  }

  public static class Audio {
    private static Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const int CLSCTX_ALL = 23;
    private const int eRender = 0;
    private const int eConsole = 0;

    private static IAudioSessionEnumerator GetEnumerator() {
      var deviceEnum = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
      IMMDevice device;
      Marshal.ThrowExceptionForHR(deviceEnum.GetDefaultAudioEndpoint(eRender, eConsole, out device));
      object o;
      Marshal.ThrowExceptionForHR(device.Activate(ref IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out o));
      var mgr = (IAudioSessionManager2)o;
      IAudioSessionEnumerator sessions;
      Marshal.ThrowExceptionForHR(mgr.GetSessionEnumerator(out sessions));
      return sessions;
    }

    private static string NameForPid(uint pid) {
      if (pid == 0) return "System Sounds";
      try {
        var p = System.Diagnostics.Process.GetProcessById((int)pid);
        return p.ProcessName;
      } catch { return "pid " + pid; }
    }

    public static List<Session> List() {
      var result = new List<Session>();
      var sessions = GetEnumerator();
      int count;
      Marshal.ThrowExceptionForHR(sessions.GetCount(out count));

      for (int i = 0; i < count; i++) {
        IAudioSessionControl ctl;
        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
        try {
          var ctl2 = ctl as IAudioSessionControl2;
          if (ctl2 == null) continue;

          uint pid; ctl2.GetProcessId(out pid);
          // pid 0 is the authoritative signal; IsSystemSoundsSession is only a
          // corroborating hint (S_OK means yes, S_FALSE means no).
          bool isSystem = (pid == 0) || (ctl2.IsSystemSoundsSession() == 0 && pid == 0);

          int state; ctl2.GetState(out state);
          string display = null;
          try { ctl2.GetDisplayName(out display); } catch { }

          var vol = ctl as ISimpleAudioVolume;
          bool muted = false; float level = 1f;
          if (vol != null) { vol.GetMute(out muted); vol.GetMasterVolume(out level); }

          result.Add(new Session {
            ProcessId = pid,
            ProcessName = isSystem ? "System Sounds" : NameForPid(pid),
            DisplayName = string.IsNullOrEmpty(display) ? null : display,
            Muted = muted,
            Volume = level,
            State = state,
            SystemSounds = isSystem
          });
        } finally {
          Marshal.ReleaseComObject(ctl);
        }
      }
      return result;
    }

    /// Applies to every session belonging to the pid - browsers and games
    /// routinely open more than one.
    public static int SetMute(uint pid, bool mute) {
      int changed = 0;
      var sessions = GetEnumerator();
      int count;
      Marshal.ThrowExceptionForHR(sessions.GetCount(out count));
      var ctx = Guid.Empty;

      for (int i = 0; i < count; i++) {
        IAudioSessionControl ctl;
        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
        try {
          var ctl2 = ctl as IAudioSessionControl2;
          if (ctl2 == null) continue;
          uint spid; ctl2.GetProcessId(out spid);
          if (spid != pid) continue;
          var vol = ctl as ISimpleAudioVolume;
          if (vol != null && vol.SetMute(mute, ref ctx) == 0) changed++;
        } finally {
          Marshal.ReleaseComObject(ctl);
        }
      }
      return changed;
    }

    public static int SetVolume(uint pid, float level) {
      int changed = 0;
      var sessions = GetEnumerator();
      int count;
      Marshal.ThrowExceptionForHR(sessions.GetCount(out count));
      var ctx = Guid.Empty;
      if (level < 0f) level = 0f;
      if (level > 1f) level = 1f;

      for (int i = 0; i < count; i++) {
        IAudioSessionControl ctl;
        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
        try {
          var ctl2 = ctl as IAudioSessionControl2;
          if (ctl2 == null) continue;
          uint spid; ctl2.GetProcessId(out spid);
          if (spid != pid) continue;
          var vol = ctl as ISimpleAudioVolume;
          if (vol != null && vol.SetMasterVolume(level, ref ctx) == 0) changed++;
        } finally {
          Marshal.ReleaseComObject(ctl);
        }
      }
      return changed;
    }
  }
}
'@

function Write-Json($obj) {
  # Depth matters: the session list is an array of objects.
  [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 4 -Compress))
  [Console]::Out.Flush()
}

function Get-SessionList {
  $sessions = [LightRec.Audio]::List()
  $out = @()
  foreach ($s in $sessions) {
    # Expired sessions belong to dead processes; nobody wants them in a mixer.
    if ($s.State -eq 2) { continue }
    # Windows often reports a display name as an unexpanded resource
    # reference like "@%SystemRoot%\System32\AudioSrv.Dll,-202". Showing that
    # in a mixer is worse than showing nothing, so fall back to the process.
    $display = $s.DisplayName
    if ([string]::IsNullOrWhiteSpace($display) -or $display.StartsWith('@')) { $display = $null }

    $out += [pscustomobject]@{
      pid          = [int]$s.ProcessId
      name         = $s.ProcessName
      displayName  = $display
      muted        = [bool]$s.Muted
      volume       = [math]::Round([double]$s.Volume, 3)
      active       = ($s.State -eq 1)
      systemSounds = [bool]$s.SystemSounds
    }
  }
  return @($out)
}

function Invoke-Action($act, $procId, $val) {
  switch ($act) {
    'list'   { return @{ ok = $true; sessions = (Get-SessionList) } }
    'mute'   { return @{ ok = $true; changed = [LightRec.Audio]::SetMute([uint32]$procId, $true) } }
    'unmute' { return @{ ok = $true; changed = [LightRec.Audio]::SetMute([uint32]$procId, $false) } }
    'volume' { return @{ ok = $true; changed = [LightRec.Audio]::SetVolume([uint32]$procId, [float]$val) } }
    default  { return @{ ok = $false; error = "unknown action: $act" } }
  }
}

if ($Action -eq 'serve') {
  Write-Json @{ ok = $true; ready = $true }
  while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }          # stdin closed - parent went away
    $line = $line.Trim()
    if (-not $line) { continue }
    if ($line -eq 'quit') { break }

    $parts = $line -split '\s+'
    $id = 0; $v = 1.0
    if ($parts.Count -gt 1) { [void][int]::TryParse($parts[1], [ref]$id) }
    if ($parts.Count -gt 2) { [void][double]::TryParse($parts[2], [ref]$v) }

    try   { Write-Json (Invoke-Action $parts[0] $id $v) }
    catch { Write-Json @{ ok = $false; error = $_.Exception.Message } }
  }
  exit 0
}

try {
  Write-Json (Invoke-Action $Action $ProcId $Value)
  exit 0
} catch {
  Write-Json @{ ok = $false; error = $_.Exception.Message }
  exit 1
}
