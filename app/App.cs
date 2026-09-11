using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LightRecorder.Ui;

namespace LightRecorder {

  /// <summary>
  /// Orchestration: windows, hotkeys, the tray, and the recording lifecycle.
  ///
  /// Deliberately does no media work itself. Video goes GPU to ffmpeg to disk;
  /// audio is captured and mixed by the WASAPI engine and piped straight out.
  /// This class only ever moves small control messages around, which is why
  /// memory stays flat during a multi-hour recording.
  /// </summary>
  internal class App : IDisposable {

    public static App Current;

    // ---- engines ----
    public readonly Recorder Rec = new Recorder();
    public readonly TabRecorder TabRec = new TabRecorder();
    public readonly Bridge Bridge = new Bridge();
    public readonly SessionMixer Mixer = new SessionMixer();
    public readonly AudioEngine Audio = new AudioEngine();

    public EncoderSet EncoderInfo = new EncoderSet();
    public Source CurrentSource;

    /// <summary>"native" while ffmpeg is recording, "tab" while the extension
    /// is, null when idle.</summary>
    public string ActiveMode;

    public bool OverlayCompact;
    public long ProgressBytes;

    // ---- windows ----
    OverlayForm _overlay;
    MixerWindow _mixer;
    PickerForm _picker;
    SettingsForm _settings;
    ToastHost _toasts;

    NotifyIcon _tray;
    HotkeyManager _hotkeys;
    Control _sync;

    System.Windows.Forms.Timer _autoHide;
    bool _quitting;
    Action _autostartChanged;

    // ------------------------------------------------------------- lifecycle

    public void Start(string initialCommand, bool atLogon) {
      Current = this;

      _sync = new Control();
      _sync.CreateControl();                 // a handle to marshal onto the UI thread

      Settings s = Settings.Current;

      _toasts = new ToastHost();
      _overlay = new OverlayForm(this);
      _toasts.AnchorTo(delegate { return _overlay.PillScreenBounds; });

      CurrentSource = Sources.DefaultSource();

      _hotkeys = new HotkeyManager();
      _hotkeys.Pressed += RunCommand;
      _hotkeys.Apply(s.Hotkeys);
      ReportHotkeyConflicts();

      WireRecorder();
      WireBridge();
      CreateTray();

      Bridge.Start(s.BridgePort);

      // Started by the logon task: the shortcuts are what was wanted, not a
      // recorder appearing on screen at every sign-in.
      if (!atLogon) _overlay.ShowInactive();

      // The listener owns the show/hide shortcut and is what starts this
      // process when it is pressed. A manual launch makes sure it is up too.
      if (s.StartWithWindows) Listener.EnsureRunning();

      // Checked on every start, so a copy of the app that has moved re-points
      // the task and a lost registration repairs itself.
      _autostartChanged = delegate { Post(NotifyState); };
      Autostart.Changed += _autostartChanged;
      SyncAutostart();

      // Encoder detection runs a couple of real two-frame encodes, so it goes
      // on a worker rather than delaying the overlay appearing.
      ThreadPool.QueueUserWorkItem(delegate {
        EncoderSet set = Encoders.Detect(Settings.Current.FfmpegPath, false);
        Post(delegate {
          EncoderInfo = set;
          if (set.Ffmpeg == null)
            Toast("ffmpeg was not found - set its path in Settings before recording.", ToastKind.Warn);
          NotifyState();

          if (!string.IsNullOrEmpty(initialCommand) &&
              initialCommand != "launch" && initialCommand != "toggleOverlay")
            RunCommand(initialCommand);

          // Startup touches far more memory than running does: JIT, the
          // settings parse, and two real ffmpeg probes. None of those pages are
          // read again, so hand them back now rather than carrying them for the
          // rest of the session.
          Native.TrimWorkingSet();
        });
      });

      Settings.Changed += OnSettingsChanged;
    }

    void ReportHotkeyConflicts() {
      if (_hotkeys.Conflicts.Count == 0) return;
      // Combinations another app already owns silently never fire, which is
      // otherwise impossible to diagnose.
      Toast("Another application already owns " +
            string.Join(", ", _hotkeys.Conflicts.ToArray()) +
            ". Change it under Settings.", ToastKind.Warn);
    }

    /// <summary>Marshal onto the UI thread. Recorder and bridge events arrive
    /// on worker threads and must not touch a window directly.</summary>
    public void Post(MethodInvoker action) {
      try {
        if (_sync == null || _sync.IsDisposed) return;
        if (_sync.InvokeRequired) _sync.BeginInvoke(action);
        else action();
      } catch (Exception) { /* shutting down */ }
    }

    void OnSettingsChanged() {
      Post(delegate {
        if (Audio.IsRunning) Audio.UpdateGains(Settings.Current);
        NotifyState();
      });
    }

    public void Dispose() {
      _quitting = true;
      Settings.Changed -= OnSettingsChanged;
      if (_autostartChanged != null) Autostart.Changed -= _autostartChanged;

      if (Rec.IsRecording || TabRec.IsRecording) StopRecording();

      try { Mixer.RestoreAll(); } catch (Exception) { }
      try { Bridge.RestoreTabMutes(); Bridge.Stop(); } catch (Exception) { }
      try { Audio.Stop(); } catch (Exception) { }

      if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; }
      if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
      if (_autoHide != null) { _autoHide.Dispose(); _autoHide = null; }

      CloseWindow(ref _picker);
      CloseWindow(ref _settings);
      if (_mixer != null) { _mixer.Dispose(); _mixer = null; }
      if (_overlay != null) { _overlay.Dispose(); _overlay = null; }
      if (_toasts != null) { _toasts.Dispose(); _toasts = null; }
      if (_sync != null) { _sync.Dispose(); _sync = null; }
    }

    static void CloseWindow<T>(ref T window) where T : Form {
      if (window == null) return;
      try { window.Close(); window.Dispose(); } catch (Exception) { }
      window = null;
    }

    // -------------------------------------------------------------- state

    public bool IsRecording { get { return Rec.IsRecording || TabRec.IsRecording; } }

    public bool IsStarting {
      get {
        return Rec.State == Recorder.RecState.Starting || TabRec.State == Recorder.RecState.Starting;
      }
    }

    public bool IsPaused {
      get { return IsRecording && (ActiveMode == "tab" ? TabRec.IsPaused : Rec.IsPaused); }
    }

    /// <summary>Recorded time, which stands still while paused.</summary>
    public long ElapsedMs {
      get { return ActiveMode == "tab" ? TabRec.ElapsedMs : Rec.ElapsedMs; }
    }

    /// <summary>Push a fresh render to every open window.</summary>
    public void NotifyState() {
      if (_overlay != null) _overlay.OnStateChanged();
      if (_mixer != null && !_mixer.IsDisposed) _mixer.OnStateChanged();
      if (_settings != null && !_settings.IsDisposed) _settings.OnStateChanged();
      if (_picker != null && !_picker.IsDisposed) _picker.OnStateChanged();
    }

    public void Toast(string message, ToastKind kind) {
      Post(delegate { if (_toasts != null) _toasts.Show(message, kind); });
    }

    // ------------------------------------------------------------- commands

    /// <summary>Act on a command, whether it came from a hotkey, the tray, or
    /// the command line of a second launch.</summary>
    public void RunCommand(string name) {
      Post(delegate {
        switch (name) {
          case "launch":
          case "toggleOverlay":
            ToggleOverlay();
            break;

          case "show":
            ShowOverlay();
            break;

          case "startStop":
            if (IsRecording) StopRecording();
            else { ShowOverlay(); StartRecording(); }
            break;

          case "record":
            if (!IsRecording) { ShowOverlay(); StartRecording(); }
            break;

          case "stop":
            if (IsRecording) StopRecording();
            break;

          case "togglePause":
            TogglePause();
            break;

          case "toggleMic":
            ToggleMic();
            break;

          case "picker":
            ShowOverlay();
            OpenPicker();
            break;

          case "toggleCompact":
            if (IsRecording) SetCompact(!OverlayCompact);
            break;

          case "settings":
            ShowOverlay();
            OpenSettings();
            break;

          case "mixer":
            ShowOverlay();
            ToggleMixer();
            break;

          case "toggleSystemAudio":
            ToggleSystemAudio();
            break;

          case "quit":
            Quit();
            break;
        }
      });
    }

    // -------------------------------------------------------------- windows

    public void ShowOverlay() {
      CancelAutoHide();
      if (_overlay == null) return;
      if (!_overlay.IsShown) { _overlay.ApplyLayout(); _overlay.ShowInactive(); }
      else Native.KeepOnTop(_overlay.Handle);
    }

    public void ToggleOverlay() {
      if (_overlay == null) return;
      if (_overlay.IsShown) {
        // Never let the panel vanish mid-recording without the user knowing.
        if (IsRecording) { Native.KeepOnTop(_overlay.Handle); return; }
        HideOverlay();
      } else {
        ShowOverlay();
      }
    }

    public void HideOverlay() {
      CancelAutoHide();
      CloseMixer();
      if (_overlay != null) { _overlay.ClearSaved(); _overlay.Hide(); }
      if (_toasts != null) _toasts.Hide();

      // With the listener resident there is no reason for this process to be:
      // the shortcut brings it straight back. Windows the user still has open
      // keep it alive until they are closed.
      if (!IsRecording && Listener.IsRunning &&
          (_picker == null || _picker.IsDisposed) && (_settings == null || _settings.IsDisposed)) {
        Quit();
        return;
      }
      // Sitting in the tray should cost as close to nothing as possible.
      Native.TrimWorkingSet();
    }

    public void SetCompact(bool compact) {
      if (OverlayCompact == compact) return;
      OverlayCompact = compact;
      if (_overlay != null) _overlay.ApplyLayout();
      RepositionMixer();
      NotifyState();
    }

    public void OpenPicker() {
      if (_picker != null && !_picker.IsDisposed) { _picker.Activate(); return; }
      _picker = new PickerForm(this);
      _picker.FormClosed += delegate {
        _picker = null;
        Native.TrimWorkingSet();
      };
      _picker.Show();
    }

    public void OpenSettings() {
      if (_settings != null && !_settings.IsDisposed) { _settings.Activate(); return; }
      _settings = new SettingsForm(this);
      _settings.FormClosed += delegate {
        _settings = null;
        Native.TrimWorkingSet();
      };
      _settings.Show();
    }

    public void ToggleMixer() {
      if (_mixer != null && !_mixer.IsDisposed) { CloseMixer(); return; }
      _mixer = new MixerWindow(this);
      _mixer.Open(_overlay != null ? _overlay.PillScreenBounds : Rectangle.Empty);
    }

    public void CloseMixer() {
      if (_mixer == null) return;
      MixerWindow m = _mixer;
      _mixer = null;
      try { m.Hide(); m.Dispose(); } catch (Exception) { }
      Native.TrimWorkingSet();
    }

    public void RepositionMixer() {
      if (_mixer == null || _mixer.IsDisposed || _overlay == null) return;
      _mixer.Reposition(_overlay.PillScreenBounds);
      _mixer.Redraw();
    }

    // ------------------------------------------------------------ recording

    public void StartRecording() {
      if (IsRecording) return;

      Settings s = Settings.Current;
      if (CurrentSource == null) CurrentSource = Sources.DefaultSource();

      try {
        Directory.CreateDirectory(s.OutputDir);
        // Prove it is writable now rather than after the user has recorded for
        // an hour into a folder they cannot write to.
        string probe = Path.Combine(s.OutputDir, ".lightrecorder-write-test");
        File.WriteAllBytes(probe, new byte[0]);
        File.Delete(probe);
      } catch (Exception) {
        Toast("Cannot write to " + s.OutputDir + ". Pick another folder in Settings.", ToastKind.Error);
        return;
      }

      if (CurrentSource.Kind == "tab") { StartTabRecording(s); return; }

      if (CurrentSource.Kind == "window") {
        // Where the window is now, not where it was when it was picked.
        string why;
        if (!Sources.ResolveWindow(CurrentSource, out why)) {
          Toast(why, ToastKind.Error);
          return;
        }
        NotifyState();       // the label may have followed the title
      }

      if (EncoderInfo.Ffmpeg == null) {
        Toast("ffmpeg was not found. Set its path in Settings.", ToastKind.Error);
        return;
      }
      EncoderInfo encoder = EncoderInfo.Find(EncoderInfo.Pick(s.Encoder));
      if (encoder == null) {
        Toast("No usable video encoder was found.", ToastKind.Error);
        return;
      }

      ActiveMode = "native";
      ProgressBytes = 0;

      bool wantAudio = s.WantsAnyAudio;
      if (wantAudio) {
        if (!Audio.Start(s, Rec.WriteAudio)) {
          Toast("Audio unavailable; recording video only.", ToastKind.Warn);
          wantAudio = false;
        }
      }

      string error;
      if (!Rec.Start(CurrentSource, s, encoder, EncoderInfo.Ffmpeg, EncoderInfo.HasDdagrab, wantAudio, out error)) {
        ActiveMode = null;
        Audio.Stop();
        Toast("Could not start: " + error, ToastKind.Error);
        NotifyState();
        return;
      }

      SetTrayState();
      SetCompact(true);      // shrink to the essentials now capture is under way
      NotifyState();
    }

    void StartTabRecording(Settings s) {
      if (!Bridge.IsConnected) {
        Toast("Browser extension is not connected, so that tab cannot be recorded.", ToastKind.Error);
        return;
      }

      string error;
      if (!TabRec.Start(s, out error)) {
        Toast("Could not start: " + error, ToastKind.Error);
        return;
      }

      ActiveMode = "tab";
      ProgressBytes = 0;
      // Recording one tab means only that tab's audio is captured, so tab B is
      // silent in the file without muting anything the user can hear.
      if (!Bridge.StartTabCapture(CurrentSource.TabId, s)) {
        // Connected a moment ago and gone now. Better told than left with a
        // timer running over an empty file.
        ActiveMode = null;
        TabRec.Stop();
        Toast("The browser extension disconnected before capture could start.", ToastKind.Error);
        return;
      }
      SetTrayState();
      SetCompact(true);
      NotifyState();
    }

    public void StopRecording() {
      if (!IsRecording) return;

      Settings s = Settings.Current;
      RecordResult result;

      if (ActiveMode == "tab") {
        Bridge.StopTabCapture();
        // Give the extension a moment to flush its final MediaRecorder chunk.
        Thread.Sleep(400);
        result = TabRec.Stop();
      } else {
        // Recorder first. Stop() writes 'q' and closes the audio pipe in the
        // same breath, so both streams reach EOF at the same instant; stopping
        // the audio engine first instead leaves a third of a second of video
        // with no sound under it.
        result = Rec.Stop(9000);
        Audio.Stop();
      }

      ActiveMode = null;
      SetTrayState();
      SetCompact(false);

      // Put the user's audio back exactly how it was before we started.
      try { Mixer.RestoreAll(); } catch (Exception) { }
      Bridge.RestoreTabMutes();

      if (result != null && result.Error != null) {
        string[] lines = result.Error.Split('\n');
        Toast("Recording problem: " + lines[lines.Length - 1], ToastKind.Error);
        Log.AppendRecording("\nFAILED\n" + result.Error + "\n");
      } else if (result != null && result.File != null) {
        if (s.RemuxOnStop && EncoderInfo.Ffmpeg != null &&
            result.File.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) {
          Toast("Finalising file…", ToastKind.Info);
          string err = Recorder.Remux(EncoderInfo.Ffmpeg, result.File);
          if (err != null) Toast("Saved, but could not finalise: " + err, ToastKind.Warn);
        }
        Toast("Saved " + Path.GetFileName(result.File), ToastKind.Success);
        if (_overlay != null) _overlay.ShowSaved(result.File);
      }

      NotifyState();
      ScheduleAutoHide();
      // A finished recording is the natural moment to hand memory back.
      Native.TrimWorkingSet();
    }

    public void ToggleRecording() {
      if (IsRecording) StopRecording(); else StartRecording();
    }

    /// <summary>
    /// Pause or resume whatever is recording. It stays one file: paused time
    /// is simply left out of it, on both tracks.
    /// </summary>
    public void TogglePause() {
      if (!IsRecording) return;

      if (ActiveMode == "tab") {
        // The browser does the pausing. If it cannot be told, the file keeps
        // growing, so the clock must not pretend otherwise.
        bool ok = TabRec.IsPaused ? Bridge.ResumeTabCapture() : Bridge.PauseTabCapture();
        if (!ok) { Toast("The browser extension is not responding, so the tab cannot be paused.", ToastKind.Warn); return; }
        if (TabRec.IsPaused) TabRec.Resume(); else TabRec.Pause();
      } else {
        if (Rec.State != Recorder.RecState.Recording) return;
        // One edge, a moment ahead, read on both clocks at the same instant:
        // the pacer stops or starts at it, and ffmpeg keeps or discards each
        // frame by whether it was captured before or after it.
        long edgeTicks = Stopwatch.GetTimestamp() + Recorder.EdgeLeadMs * Stopwatch.Frequency / 1000;
        double edgeSeconds = Native.PreciseEpochSeconds() + Recorder.EdgeLeadMs / 1000.0;
        if (!Rec.IsPaused) { Audio.Pause(edgeTicks); Rec.Pause(edgeTicks, edgeSeconds); }
        else { Audio.Resume(edgeTicks); Rec.Resume(edgeTicks, edgeSeconds); }
      }

      SetTrayState();
      NotifyState();
    }

    // ---------------------------------------------------------------- audio

    public void ToggleMic() {
      Settings s = Settings.Current;
      s.MicMuted = !s.MicMuted;
      s.Save();
      Toast(s.MicMuted ? "Microphone muted" : "Microphone live", ToastKind.Info);
    }

    public void ToggleSystemAudio() {
      Settings s = Settings.Current;
      s.SystemAudioMuted = !s.SystemAudioMuted;
      s.Save();
    }

    // ------------------------------------------------------------- auto-hide

    void CancelAutoHide() {
      if (_autoHide == null) return;
      _autoHide.Stop();
    }

    /// <summary>After a recording finishes, put the overlay away by itself.</summary>
    void ScheduleAutoHide() {
      int seconds = Settings.Current.AutoHideAfterStopSeconds;
      if (seconds <= 0) return;

      if (_autoHide == null) {
        _autoHide = new System.Windows.Forms.Timer();
        _autoHide.Tick += delegate {
          _autoHide.Stop();
          if (IsRecording) return;
          if (_picker != null || _settings != null) return;
          HideOverlay();
        };
      }
      _autoHide.Stop();
      _autoHide.Interval = seconds * 1000;
      _autoHide.Start();
    }

    // ------------------------------------------------------------- plumbing

    void WireRecorder() {
      Rec.AudioPipeConnected += delegate { Audio.BeginEmitting(Rec.AnchorTicks); };

      Rec.ProgressChanged += delegate (Progress p) {
        // ffmpeg reports ten times a second now that pausing needs it to read
        // stdin that often, but the byte count only moves when a fragment is
        // written. Repainting for reports that change nothing is waste.
        if (p.Bytes == ProgressBytes) return;
        ProgressBytes = p.Bytes;
        Post(delegate { if (_overlay != null) _overlay.Redraw(); });
      };

      Rec.Started += delegate (string file, string[] args) {
        Log.Recording(string.Join(Environment.NewLine, new[] {
          "started : " + DateTime.Now.ToString("O"),
          "output  : " + file,
          "ffmpeg  : " + EncoderInfo.Ffmpeg,
          "pipeline: " + (Rec.UsedGpuPath
            ? "GPU-direct (ddagrab -> encoder, no CPU frame copies)"
            : "download (ddagrab/gdigrab -> CPU convert -> encoder)"),
          "args    : " + Recorder.JoinArgs(args),
          ""
        }));
      };

      Rec.Stopped += delegate (RecordResult r) {
        // ffmpeg dying on its own - disk full, encoder lost - must not leave
        // the UI stuck showing "recording".
        Post(delegate {
          if (ActiveMode == "native" && Rec.State == Recorder.RecState.Idle && !_quitting) {
            Audio.Stop();
            ActiveMode = null;
            SetTrayState();
            SetCompact(false);
            if (r != null && r.Error != null) {
              Toast("Recording stopped: " + LastLine(r.Error), ToastKind.Error);
              // The toast shows one line; the log keeps everything ffmpeg said.
              Log.AppendRecording("\nFAILED (ffmpeg exited on its own)\n" + r.Error + "\n");
            }
            NotifyState();
          }
        });
      };

      TabRec.ProgressChanged += delegate (Progress p) {
        ProgressBytes = p.Bytes;
        Post(delegate { if (_overlay != null) _overlay.Redraw(); });
      };
      TabRec.Failed += delegate (string m) { Toast(m, ToastKind.Error); };

      Audio.Problem += delegate (string m) { Toast("Audio: " + m, ToastKind.Warn); };
    }

    static string LastLine(string text) {
      string[] lines = text.Split('\n');
      return lines[lines.Length - 1];
    }

    void WireBridge() {
      Bridge.Connected += delegate (string browser) {
        Toast(browser + " extension connected", ToastKind.Success);
        Post(NotifyState);
      };
      Bridge.Disconnected += delegate { Post(NotifyState); };
      Bridge.TabsChanged += delegate { Post(NotifyState); };
      Bridge.Error += delegate (string m) { Toast("Bridge: " + m, ToastKind.Warn); };

      Bridge.BinaryChunk += delegate (byte[] data, int count) { TabRec.Write(data, count); };
      Bridge.TabCaptureStarted += delegate (string mime) { TabRec.SetContainerFromMime(mime); };

      Bridge.TabCaptureError += delegate (string message) {
        Toast("Tab capture failed: " + message, ToastKind.Error);
        Post(delegate { if (ActiveMode == "tab") StopRecording(); });
      };

      Bridge.TabCaptureStopped += delegate {
        Post(delegate { if (ActiveMode == "tab" && TabRec.IsRecording) StopRecording(); });
      };

      // The chunks come over their own socket. If that drops mid-take - the
      // browser closed, the extension reloaded - nothing else would say so,
      // and the timer would keep counting over a file that stopped growing.
      Bridge.DataClosed += delegate {
        Post(delegate {
          if (ActiveMode != "tab" || !TabRec.IsRecording) return;
          Toast("The browser stopped sending the tab. Saving what arrived.", ToastKind.Warn);
          StopRecording();
        });
      };

      // The user pressed "Record this tab" in the extension popup. Chrome only
      // permits tab capture from a real click inside the extension, so this
      // path is browser-initiated and the app just opens the output file.
      Bridge.TabRecordingStarting += delegate (int tabId) {
        Post(delegate {
          // The extension has already begun capturing by the time this
          // arrives; if the app cannot take the stream, it has to say stop, or
          // the browser carries on encoding a tab nobody is writing to disk.
          if (IsRecording) {
            Bridge.StopTabCapture();
            Toast("Already recording - stop first to record that tab.", ToastKind.Warn);
            return;
          }
          TabInfo tab = Bridge.FindTab(tabId);

          var src = new Source();
          src.Kind = "tab";
          src.Id = "tab:" + tabId;
          src.TabId = tabId;
          src.Name = tab != null ? tab.Title : "Browser tab";
          CurrentSource = src;

          string error;
          if (!TabRec.Start(Settings.Current, out error)) {
            Bridge.StopTabCapture();
            Toast("Could not start tab recording: " + error, ToastKind.Error);
            return;
          }
          ActiveMode = "tab";
          ProgressBytes = 0;
          SetTrayState();
          ShowOverlay();
          SetCompact(true);
          NotifyState();
        });
      };
    }

    // ----------------------------------------------------------------- tray

    void CreateTray() {
      try {
        _tray = new NotifyIcon();
        _tray.Icon = TrayIcons.Make(false, false);
        _tray.Text = "Light Recorder";
        _tray.Visible = true;
        _tray.MouseClick += delegate (object sender, MouseEventArgs e) {
          if (e.Button == MouseButtons.Left) ToggleOverlay();
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
      } catch (Exception err) {
        Log.Warn("tray unavailable: " + err.Message);   // a convenience, not a requirement
      }
    }

    ContextMenuStrip BuildTrayMenu() {
      var menu = new ContextMenuStrip();
      menu.Renderer = new DarkMenuRenderer();
      menu.BackColor = Theme.BgPanel;
      menu.ForeColor = Theme.Fg;
      menu.ShowImageMargin = false;

      menu.Items.Add(Item("Show recorder", delegate { ShowOverlay(); }));
      menu.Items.Add(Item("Choose source…", delegate { ShowOverlay(); OpenPicker(); }));
      menu.Items.Add(new ToolStripSeparator());
      ToolStripMenuItem start = Item("Start recording", delegate { ShowOverlay(); StartRecording(); });
      ToolStripMenuItem pause = Item("Pause recording", delegate { TogglePause(); });
      ToolStripMenuItem stop = Item("Stop recording", delegate { StopRecording(); });
      menu.Items.Add(start);
      menu.Items.Add(pause);
      menu.Items.Add(stop);
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(Item("Open save folder", delegate { OpenSaveFolder(); }));
      menu.Items.Add(Item("Settings…", delegate { OpenSettings(); }));
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(Item("Quit", delegate { Quit(); }));

      menu.Opening += delegate {
        start.Enabled = !IsRecording;
        pause.Enabled = IsRecording;
        pause.Text = IsPaused ? "Resume recording" : "Pause recording";
        stop.Enabled = IsRecording;
      };
      return menu;
    }

    static ToolStripMenuItem Item(string text, EventHandler onClick) {
      var item = new ToolStripMenuItem(text);
      item.Click += onClick;
      return item;
    }

    /// <summary>Red while recording, amber while paused, so the state shows
    /// even with the overlay collapsed or covered.</summary>
    void SetTrayState() {
      if (_tray == null) return;
      try {
        Icon previous = _tray.Icon;
        _tray.Icon = TrayIcons.Make(IsRecording, IsPaused);
        _tray.Text = IsPaused ? "Light Recorder - paused"
                   : IsRecording ? "Light Recorder - recording" : "Light Recorder";
        if (previous != null) previous.Dispose();
      } catch (Exception) { }
    }

    public void OpenSaveFolder() {
      try {
        Directory.CreateDirectory(Settings.Current.OutputDir);
        Process.Start("explorer.exe", "\"" + Settings.Current.OutputDir + "\"");
      } catch (Exception err) {
        Toast("Could not open the folder: " + err.Message, ToastKind.Warn);
      }
    }

    public void OpenExtensionFolder() {
      try {
        string dir = Paths.ExtensionDir;
        if (!Directory.Exists(dir)) { Toast("The extension folder is not next to the app.", ToastKind.Warn); return; }
        Process.Start("explorer.exe", "\"" + dir + "\"");
      } catch (Exception err) {
        Toast("Could not open the folder: " + err.Message, ToastKind.Warn);
      }
    }

    public void Quit() {
      _quitting = true;
      Application.Exit();
    }

    // ------------------------------------------------------------- settings

    /// <summary>Shortcuts another application already claimed, so the Settings
    /// window can say so instead of leaving the user guessing.</summary>
    public List<string> HotkeyConflicts {
      get { return _hotkeys != null ? _hotkeys.Conflicts : new List<string>(); }
    }

    /// <summary>Make the logon task match the Start with Windows setting.</summary>
    public void SyncAutostart() {
      Autostart.SyncAsync(Settings.Current.StartWithWindows);
    }

    /// <summary>Re-register hotkeys after the user edits them.</summary>
    public void ReapplyHotkeys() {
      _hotkeys.Apply(Settings.Current.Hotkeys);
      Listener.Reload();               // the show/hide key is its to re-register
      ReportHotkeyConflicts();
      NotifyState();
    }

    public void RestartBridge(int port) {
      Bridge.Start(port);
      NotifyState();
    }

    public void RedetectEncoders() {
      ThreadPool.QueueUserWorkItem(delegate {
        EncoderSet set = Encoders.Detect(Settings.Current.FfmpegPath, true);
        Post(delegate {
          EncoderInfo = set;
          NotifyState();
        });
      });
    }
  }

  // ==========================================================================

  /// <summary>
  /// The tray icon, drawn rather than shipped as a file: a filled dot, red
  /// while recording and amber while paused. Two PNGs and an icon-generator
  /// script disappear with it.
  /// </summary>
  internal static class TrayIcons {

    public static Icon Make(bool recording, bool paused) {
      int size = SystemInformation.SmallIconSize.Width;
      if (size < 16) size = 16;
      if (size > 64) size = 64;

      using (var bitmap = new Bitmap(size, size)) {
        using (Graphics g = Graphics.FromImage(bitmap)) {
          g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
          g.Clear(Color.Transparent);

          float inset = size * 0.12f;
          var box = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

          if (recording) {
            using (var b = new SolidBrush(paused ? Theme.Warn : Theme.Rec)) g.FillEllipse(b, box);
          } else {
            using (var pen = new Pen(Color.FromArgb(230, 230, 235), Math.Max(1.4f, size / 11f)))
              g.DrawEllipse(pen, box);
            float dot = size * 0.30f;
            using (var b = new SolidBrush(Color.FromArgb(230, 230, 235)))
              g.FillEllipse(b, (size - dot) / 2f, (size - dot) / 2f, dot, dot);
          }
        }

        IntPtr handle = bitmap.GetHicon();
        try {
          using (Icon temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
        } finally {
          DestroyIcon(handle);
        }
      }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
  }

  /// <summary>Dark rendering for the tray menu, so it matches everything
  /// else rather than being the one light-grey surface in the app.</summary>
  internal class DarkMenuRenderer : ToolStripProfessionalRenderer {

    public DarkMenuRenderer() : base(new DarkColours()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
      e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.FgFaint;
      base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
      using (var pen = new Pen(Theme.Border)) {
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
      }
    }

    class DarkColours : ProfessionalColorTable {
      public override Color MenuItemSelected { get { return Theme.BgHover; } }
      public override Color MenuItemSelectedGradientBegin { get { return Theme.BgHover; } }
      public override Color MenuItemSelectedGradientEnd { get { return Theme.BgHover; } }
      public override Color MenuItemBorder { get { return Theme.BorderStrong; } }
      public override Color MenuBorder { get { return Theme.BorderStrong; } }
      public override Color ToolStripDropDownBackground { get { return Theme.BgPanel; } }
      public override Color ImageMarginGradientBegin { get { return Theme.BgPanel; } }
      public override Color ImageMarginGradientMiddle { get { return Theme.BgPanel; } }
      public override Color ImageMarginGradientEnd { get { return Theme.BgPanel; } }
    }
  }
}
