using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LightRecorder {

  internal class AppSession {
    public uint Pid;
    public string Name;          // what the mixer shows
    public bool Muted;
    public bool Active;          // currently rendering audio
    public bool SystemSounds;
  }

  /// <summary>
  /// Per-application audio control - the desktop half of the mixer.
  ///
  /// Windows has no way to record one app's audio in isolation without a
  /// kernel driver, so "mute app X" here means muting X's Core Audio session
  /// for the duration of the recording: X is then absent from the loopback
  /// capture. The side effect is that you also stop hearing X live. That is
  /// inherent, the UI says so, and everything we touch is restored on stop.
  ///
  /// Behaviour is identical to the PowerShell helper this replaces, but the
  /// helper cost ~60 MB and ~400 ms to compile its interop on first use; this
  /// costs a COM call and nothing at all when the mixer is closed.
  /// </summary>
  internal class SessionMixer {

    /// <summary>pid -> the mute state before we touched it.</summary>
    readonly Dictionary<uint, bool> _changedByUs = new Dictionary<uint, bool>();

    /// <summary>pid -> display name. Resolving a name means opening the
    /// process and reading its version resource, which is far too expensive to
    /// repeat every three seconds while the mixer polls.</summary>
    static readonly Dictionary<uint, string> NameCache = new Dictionary<uint, string>();

    // ------------------------------------------------------------ listing

    /// <summary>
    /// Apps currently holding an audio session, most interesting first.
    /// Collapses the many sessions a browser or game opens into one row per
    /// process, preferring an active session over an idle one.
    /// </summary>
    public List<AppSession> List(out string error) {
      error = null;
      var byPid = new Dictionary<uint, AppSession>();

      IAudioSessionEnumerator sessions = null;
      IMMDeviceEnumerator en = null;
      IMMDevice device = null;
      IAudioSessionManager2 manager = null;

      try {
        sessions = OpenEnumerator(out en, out device, out manager);
        if (sessions == null) { error = "no default playback device"; return new List<AppSession>(); }

        int count;
        if (!Com.Ok(sessions.GetCount(out count))) { error = "could not read audio sessions"; return new List<AppSession>(); }

        for (int i = 0; i < count; i++) {
          IAudioSessionControl2 ctl = null;
          try {
            if (!Com.Ok(sessions.GetSession(i, out ctl)) || ctl == null) continue;

            uint pid;
            if (!Com.Ok(ctl.GetProcessId(out pid))) continue;

            // pid 0 is the authoritative signal for the system-sounds session;
            // IsSystemSoundsSession returns S_FALSE (1) for ordinary apps, so
            // it corroborates rather than decides.
            bool isSystem = pid == 0;

            int state;
            ctl.GetState(out state);

            var vol = ctl as ISimpleAudioVolume;
            bool muted = false;
            if (vol != null) vol.GetMute(out muted);

            string display = null;
            try { ctl.GetDisplayName(out display); } catch (Exception) { }

            var row = new AppSession();
            row.Pid = pid;
            row.SystemSounds = isSystem;
            row.Muted = muted;
            row.Active = state == SessionState.Active;
            row.Name = isSystem ? "System Sounds"
                                : (!string.IsNullOrEmpty(display) ? display : NameForPid(pid));

            AppSession prev;
            if (!byPid.TryGetValue(pid, out prev) || (row.Active && !prev.Active))
              byPid[pid] = row;

          } catch (Exception) {
            // One unreadable session must not cost the whole list.
          } finally {
            Com.Release(ctl);
          }
        }
      } catch (Exception err) {
        error = err.Message;
      } finally {
        Com.Release(sessions);
        Com.Release(manager);
        Com.Release(device);
        Com.Release(en);
      }

      var list = new List<AppSession>(byPid.Values);
      list.Sort(delegate (AppSession a, AppSession b) {
        if (a.Active != b.Active) return a.Active ? -1 : 1;
        return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.CurrentCultureIgnoreCase);
      });
      return list;
    }

    // ------------------------------------------------------------- muting

    /// <summary>
    /// Mute or unmute every session belonging to a pid - browsers and games
    /// routinely open more than one. The first time we touch a process its
    /// previous state is remembered so RestoreAll can put it back.
    /// </summary>
    public bool SetMuted(uint pid, bool muted) {
      IAudioSessionEnumerator sessions = null;
      IMMDeviceEnumerator en = null;
      IMMDevice device = null;
      IAudioSessionManager2 manager = null;
      int changed = 0;

      try {
        sessions = OpenEnumerator(out en, out device, out manager);
        if (sessions == null) return false;

        int count;
        if (!Com.Ok(sessions.GetCount(out count))) return false;

        for (int i = 0; i < count; i++) {
          IAudioSessionControl2 ctl = null;
          try {
            if (!Com.Ok(sessions.GetSession(i, out ctl)) || ctl == null) continue;

            uint spid;
            if (!Com.Ok(ctl.GetProcessId(out spid)) || spid != pid) continue;

            var vol = ctl as ISimpleAudioVolume;
            if (vol == null) continue;

            if (!_changedByUs.ContainsKey(pid)) {
              bool before;
              vol.GetMute(out before);
              _changedByUs[pid] = before;
            }

            if (Com.Ok(vol.SetMute(muted, IntPtr.Zero))) changed++;
          } catch (Exception) {
          } finally {
            Com.Release(ctl);
          }
        }
      } catch (Exception err) {
        Log.Warn("mixer: could not set mute on pid " + pid + ": " + err.Message);
        return false;
      } finally {
        Com.Release(sessions);
        Com.Release(manager);
        Com.Release(device);
        Com.Release(en);
      }

      return changed > 0;
    }

    /// <summary>Put every app we muted back the way the user had it.</summary>
    public void RestoreAll() {
      if (_changedByUs.Count == 0) return;
      var snapshot = new List<KeyValuePair<uint, bool>>(_changedByUs);
      // Cleared first so a failure part-way through cannot leave us trying to
      // restore the same pid forever.
      _changedByUs.Clear();
      foreach (KeyValuePair<uint, bool> kv in snapshot) SetMutedRaw(kv.Key, kv.Value);
    }

    /// <summary>Restore path: sets mute without recording a "before" state.</summary>
    void SetMutedRaw(uint pid, bool muted) {
      IAudioSessionEnumerator sessions = null;
      IMMDeviceEnumerator en = null;
      IMMDevice device = null;
      IAudioSessionManager2 manager = null;

      try {
        sessions = OpenEnumerator(out en, out device, out manager);
        if (sessions == null) return;

        int count;
        if (!Com.Ok(sessions.GetCount(out count))) return;

        for (int i = 0; i < count; i++) {
          IAudioSessionControl2 ctl = null;
          try {
            if (!Com.Ok(sessions.GetSession(i, out ctl)) || ctl == null) continue;
            uint spid;
            if (!Com.Ok(ctl.GetProcessId(out spid)) || spid != pid) continue;
            var vol = ctl as ISimpleAudioVolume;
            if (vol != null) vol.SetMute(muted, IntPtr.Zero);
          } catch (Exception) {
          } finally {
            Com.Release(ctl);
          }
        }
      } catch (Exception) {
      } finally {
        Com.Release(sessions);
        Com.Release(manager);
        Com.Release(device);
        Com.Release(en);
      }
    }

    public bool HasChanges { get { return _changedByUs.Count > 0; } }

    // ------------------------------------------------------------- plumbing

    static IAudioSessionEnumerator OpenEnumerator(out IMMDeviceEnumerator en,
                                                  out IMMDevice device,
                                                  out IAudioSessionManager2 manager) {
      en = null; device = null; manager = null;

      en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
      if (!Com.Ok(en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device)) || device == null)
        return null;

      object o;
      Guid iid = Com.IID_IAudioSessionManager2;
      if (!Com.Ok(device.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out o)) || o == null)
        return null;

      manager = (IAudioSessionManager2)o;
      IAudioSessionEnumerator sessions;
      if (!Com.Ok(manager.GetSessionEnumerator(out sessions))) return null;
      return sessions;
    }

    /// <summary>
    /// A readable name for a pid. The version resource's FileDescription is
    /// what Task Manager shows ("Google Chrome" rather than "chrome"), so it is
    /// tried first; a protected or already-exited process falls back to the
    /// process name and then to the raw pid.
    /// </summary>
    static string NameForPid(uint pid) {
      string cached;
      if (NameCache.TryGetValue(pid, out cached)) return cached;

      string name = "pid " + pid;
      try {
        Process p = Process.GetProcessById((int)pid);
        name = p.ProcessName;
        try {
          string exe = p.MainModule.FileName;
          if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrEmpty(info.FileDescription)) name = info.FileDescription.Trim();
          }
        } catch (Exception) {
          // Elevated or 32/64-bit mismatch: the process name is still fine.
        }
      } catch (Exception) {
        // Already gone by the time we asked.
      }

      // Bounded: a long session with a lot of process churn should not turn
      // this into a leak.
      if (NameCache.Count > 256) NameCache.Clear();
      NameCache[pid] = name;
      return name;
    }
  }
}
