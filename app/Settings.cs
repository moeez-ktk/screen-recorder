using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LightRecorder {

  /// <summary>
  /// Where everything on disk lives. Deliberately the same folder the Electron
  /// build used, so an upgrade keeps the user's settings, their save folder and
  /// their extension pairing token.
  /// </summary>
  internal static class Paths {
    static string _userData;

    public static string UserData {
      get {
        if (_userData == null) {
          _userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Light Recorder");
          try { Directory.CreateDirectory(_userData); } catch (Exception) { }
        }
        return _userData;
      }
    }

    public static string SettingsFile { get { return Path.Combine(UserData, "settings.json"); } }
    public static string EncoderCache { get { return Path.Combine(UserData, "encoder-cache.json"); } }
    public static string BridgeToken { get { return Path.Combine(UserData, "bridge-token.txt"); } }
    public static string RecordingLog { get { return Path.Combine(UserData, "last-recording.log"); } }

    public static string DefaultOutputDir {
      get {
        return Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
          "Light Recorder");
      }
    }

    /// <summary>The folder holding the app, used to find the bundled extension.</summary>
    public static string AppDir {
      get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
    }

    public static string ExtensionDir {
      get {
        // Running from the repo the exe sits in build\, so the extension is one
        // level up; installed, it sits alongside.
        string here = Path.Combine(AppDir, "extension");
        if (Directory.Exists(here)) return here;
        string up = Path.Combine(Path.GetDirectoryName(AppDir) ?? AppDir, "extension");
        return up;
      }
    }
  }

  internal class Hotkeys {
    public string ToggleOverlay = "Control+Shift+D";
    public string StartStop = "Control+Alt+S";
    /// <summary>Loom's pause shortcut. Ctrl+Alt+Space was the first choice and
    /// turned out to be claimed on the very first machine it was tried on.</summary>
    public string TogglePause = "Alt+Shift+P";
    public string Stop = "Control+Alt+X";
    public string ToggleMic = "Control+Alt+M";
    public string Picker = "Control+Alt+P";

    public Hotkeys Clone() {
      var h = new Hotkeys();
      h.ToggleOverlay = ToggleOverlay; h.StartStop = StartStop; h.TogglePause = TogglePause;
      h.Stop = Stop; h.ToggleMic = ToggleMic; h.Picker = Picker;
      return h;
    }

    public string Get(string name) {
      switch (name) {
        case "toggleOverlay": return ToggleOverlay;
        case "startStop": return StartStop;
        case "togglePause": return TogglePause;
        case "stop": return Stop;
        case "toggleMic": return ToggleMic;
        case "picker": return Picker;
      }
      return null;
    }

    public void Set(string name, string accel) {
      switch (name) {
        case "toggleOverlay": ToggleOverlay = accel; break;
        case "startStop": StartStop = accel; break;
        case "togglePause": TogglePause = accel; break;
        case "stop": Stop = accel; break;
        case "toggleMic": ToggleMic = accel; break;
        case "picker": Picker = accel; break;
      }
    }

    /// <summary>Each name doubles as the command the shortcut runs.</summary>
    public static readonly string[] Names =
      { "toggleOverlay", "startStop", "togglePause", "stop", "toggleMic", "picker" };

    public static readonly string[] Labels = {
      "Show / hide the overlay",
      "Start or stop recording",
      "Pause / resume recording",
      "Stop recording",
      "Mute / unmute microphone",
      "Choose what to record"
    };
  }

  /// <summary>
  /// Persisted user settings.
  ///
  /// Read once at start, written only when something actually changes, and
  /// serialised by hand rather than by reflection - it is thirty values, and
  /// hand-written accessors cost nothing at runtime and survive a hand-edited
  /// file without throwing.
  /// </summary>
  internal class Settings {

    /// <summary>
    /// Bumped when a stored value needs rewriting rather than merging. Stored
    /// values always win over defaults, so changing a default alone would never
    /// reach anyone who has already run the app.
    ///   2 - the launch shortcut moved to Ctrl+Shift+D
    ///   3 - microphone ids moved from Chromium's namespace to WASAPI's
    /// </summary>
    public const int CurrentVersion = 3;

    public int SettingsVersion = CurrentVersion;

    // --- output ---
    public string OutputDir = "";
    public string FilenamePattern = "Recording {date} {time}";
    public string Container = "mp4";
    public bool RemuxOnStop = false;

    // --- video ---
    public int Fps = 60;
    public string Resolution = "source";      // source | 2160 | 1440 | 1080 | 720 | 480
    public string Encoder = "auto";           // auto | h264_nvenc | h264_qsv | h264_amf | h264_mf | libx264
    public string QualityMode = "quality";    // quality (CQ) | bitrate
    public int Quality = 23;
    public int BitrateMbps = 12;
    public bool CaptureCursor = true;

    // --- audio ---
    public bool SystemAudioEnabled = true;
    public bool SystemAudioMuted = false;
    public double SystemAudioGain = 1.0;
    public bool MicEnabled = true;
    public bool MicMuted = false;
    public double MicGain = 1.0;
    public string MicDeviceId = "default";
    /// <summary>Automatic gain on the microphone, as call apps and browsers
    /// apply by default. See VoiceLeveler.</summary>
    public bool MicAutoLevel = true;
    public int AudioBitrateKbps = 160;

    // --- lifecycle ---
    /// <summary>0 keeps the overlay up until it is dismissed.</summary>
    public int AutoHideAfterStopSeconds = 20;
    /// <summary>On by default: the shortcuts are the app, and they are gone
    /// after a reboot unless something starts it again.</summary>
    public bool StartWithWindows = true;

    // --- behaviour ---
    public int OverlayOffsetX = 16;
    public int OverlayOffsetY = 16;
    public bool HasCustomPos = false;
    public int OverlayPosX = 0;
    public int OverlayPosY = 0;
    public int BridgePort = 8787;
    public string FfmpegPath = "";

    public Hotkeys Hotkeys = new Hotkeys();

    // ------------------------------------------------------------ lifecycle

    static Settings _current;
    public static Settings Current {
      get { if (_current == null) _current = Load(); return _current; }
    }

    /// <summary>Raised after any save, so open windows can re-render.</summary>
    public static event Action Changed;

    public static Settings Load() {
      var s = new Settings();
      Dictionary<string, object> stored = null;
      try {
        if (File.Exists(Paths.SettingsFile))
          stored = Json.ParseObject(File.ReadAllText(Paths.SettingsFile));
      } catch (Exception) { /* first run, or a file we cannot read - use defaults */ }

      int storedVersion = 1;
      if (stored != null && stored.Count > 0) {
        storedVersion = Json.Int(stored, "settingsVersion", 1);
        s.Apply(stored);
      }

      if (string.IsNullOrEmpty(s.OutputDir)) s.OutputDir = Paths.DefaultOutputDir;
      try { Directory.CreateDirectory(s.OutputDir); } catch (Exception) { /* reported when recording starts */ }

      _current = s;
      if (stored != null && storedVersion < CurrentVersion) { s.Migrate(storedVersion); s.Save(); }
      return s;
    }

    /// <summary>
    /// Apply changes that stored settings would otherwise shadow forever.
    /// Only touches values the user has demonstrably not customised.
    /// </summary>
    void Migrate(int from) {
      if (from < 2) {
        // The launch shortcut moved to Ctrl+Shift+D. Leave it alone if the user
        // had already picked something other than the old default.
        if (Hotkeys.ToggleOverlay == "Control+Alt+R") Hotkeys.ToggleOverlay = "Control+Shift+D";
      }
      if (from < 3) {
        // Microphones used to be identified by Chromium's device id, which is a
        // 64-char hash. WASAPI endpoint ids look like "{0.0.1.00000000}.{guid}".
        // Anything that is not one of ours can only mean the old namespace.
        if (!string.IsNullOrEmpty(MicDeviceId) && MicDeviceId != "default" && !MicDeviceId.StartsWith("{"))
          MicDeviceId = "default";
      }
      SettingsVersion = CurrentVersion;
    }

    void Apply(Dictionary<string, object> m) {
      OutputDir = Json.Str(m, "outputDir", OutputDir);
      FilenamePattern = Json.Str(m, "filenamePattern", FilenamePattern);
      Container = Json.Str(m, "container", Container);
      RemuxOnStop = Json.Bool(m, "remuxOnStop", RemuxOnStop);

      Fps = Json.Int(m, "fps", Fps);
      Resolution = Json.Str(m, "resolution", Resolution);
      Encoder = Json.Str(m, "encoder", Encoder);
      QualityMode = Json.Str(m, "qualityMode", QualityMode);
      Quality = Json.Int(m, "quality", Quality);
      BitrateMbps = Json.Int(m, "bitrateMbps", BitrateMbps);
      CaptureCursor = Json.Bool(m, "captureCursor", CaptureCursor);

      SystemAudioEnabled = Json.Bool(m, "systemAudioEnabled", SystemAudioEnabled);
      SystemAudioMuted = Json.Bool(m, "systemAudioMuted", SystemAudioMuted);
      SystemAudioGain = Json.Num(m, "systemAudioGain", SystemAudioGain);
      MicEnabled = Json.Bool(m, "micEnabled", MicEnabled);
      MicMuted = Json.Bool(m, "micMuted", MicMuted);
      MicGain = Json.Num(m, "micGain", MicGain);
      MicDeviceId = Json.Str(m, "micDeviceId", MicDeviceId);
      MicAutoLevel = Json.Bool(m, "micAutoLevel", MicAutoLevel);
      AudioBitrateKbps = Json.Int(m, "audioBitrateKbps", AudioBitrateKbps);

      AutoHideAfterStopSeconds = Json.Int(m, "autoHideAfterStopSeconds", AutoHideAfterStopSeconds);
      StartWithWindows = Json.Bool(m, "startWithWindows", StartWithWindows);

      var off = Json.Sub(m, "overlayOffset");
      if (off != null) {
        OverlayOffsetX = Json.Int(off, "x", OverlayOffsetX);
        OverlayOffsetY = Json.Int(off, "y", OverlayOffsetY);
      }
      var pos = Json.Sub(m, "overlayCustomPos");
      if (pos != null) {
        HasCustomPos = true;
        OverlayPosX = Json.Int(pos, "x", 0);
        OverlayPosY = Json.Int(pos, "y", 0);
      }

      BridgePort = Json.Int(m, "bridgePort", BridgePort);
      FfmpegPath = Json.Str(m, "ffmpegPath", FfmpegPath);

      var hk = Json.Sub(m, "hotkeys");
      if (hk != null) {
        foreach (string name in LightRecorder.Hotkeys.Names) {
          object v = Json.Get(hk, name);
          if (v is string) Hotkeys.Set(name, (string)v);
        }
      }

      SettingsVersion = Json.Int(m, "settingsVersion", SettingsVersion);
    }

    Dictionary<string, object> ToMap() {
      var m = new Dictionary<string, object>();
      m["settingsVersion"] = CurrentVersion;

      m["outputDir"] = OutputDir;
      m["filenamePattern"] = FilenamePattern;
      m["container"] = Container;
      m["remuxOnStop"] = RemuxOnStop;

      m["fps"] = Fps;
      m["resolution"] = Resolution;
      m["encoder"] = Encoder;
      m["qualityMode"] = QualityMode;
      m["quality"] = Quality;
      m["bitrateMbps"] = BitrateMbps;
      m["captureCursor"] = CaptureCursor;

      m["systemAudioEnabled"] = SystemAudioEnabled;
      m["systemAudioMuted"] = SystemAudioMuted;
      m["systemAudioGain"] = SystemAudioGain;
      m["micEnabled"] = MicEnabled;
      m["micMuted"] = MicMuted;
      m["micGain"] = MicGain;
      m["micDeviceId"] = MicDeviceId;
      m["micAutoLevel"] = MicAutoLevel;
      m["audioBitrateKbps"] = AudioBitrateKbps;

      m["autoHideAfterStopSeconds"] = AutoHideAfterStopSeconds;
      m["startWithWindows"] = StartWithWindows;

      var off = new Dictionary<string, object>();
      off["x"] = OverlayOffsetX; off["y"] = OverlayOffsetY;
      m["overlayOffset"] = off;

      if (HasCustomPos) {
        var pos = new Dictionary<string, object>();
        pos["x"] = OverlayPosX; pos["y"] = OverlayPosY;
        m["overlayCustomPos"] = pos;
      } else {
        m["overlayCustomPos"] = null;
      }

      m["bridgePort"] = BridgePort;
      m["ffmpegPath"] = FfmpegPath;

      var hk = new Dictionary<string, object>();
      for (int i = 0; i < LightRecorder.Hotkeys.Names.Length; i++) {
        string name = LightRecorder.Hotkeys.Names[i];
        hk[name] = Hotkeys.Get(name) ?? "";
      }
      m["hotkeys"] = hk;

      return m;
    }

    /// <summary>Persist and notify. Writes via a temp file so a crash mid-write
    /// cannot leave a truncated settings file behind.</summary>
    public void Save() {
      try {
        string tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, Json.WritePretty(ToMap()));
        if (File.Exists(Paths.SettingsFile)) {
          // Replace rather than delete-then-move: the delete/move pair has a
          // window where the file simply does not exist, and losing every
          // setting to a power cut is a poor trade for two lines of code.
          File.Replace(tmp, Paths.SettingsFile, null);
        } else {
          File.Move(tmp, Paths.SettingsFile);
        }
      } catch (Exception err) {
        Log.Warn("settings save failed: " + err.Message);
      }
      Action h = Changed;
      if (h != null) h();
    }

    public void ResetToDefaults() {
      string dir = OutputDir;
      var d = new Settings();
      d.OutputDir = dir;

      SettingsVersion = CurrentVersion;
      FilenamePattern = d.FilenamePattern; Container = d.Container; RemuxOnStop = d.RemuxOnStop;
      Fps = d.Fps; Resolution = d.Resolution; Encoder = d.Encoder;
      QualityMode = d.QualityMode; Quality = d.Quality; BitrateMbps = d.BitrateMbps;
      CaptureCursor = d.CaptureCursor;
      SystemAudioEnabled = d.SystemAudioEnabled; SystemAudioMuted = d.SystemAudioMuted;
      SystemAudioGain = d.SystemAudioGain; MicEnabled = d.MicEnabled; MicMuted = d.MicMuted;
      MicGain = d.MicGain; MicDeviceId = d.MicDeviceId; MicAutoLevel = d.MicAutoLevel;
      AudioBitrateKbps = d.AudioBitrateKbps;
      AutoHideAfterStopSeconds = d.AutoHideAfterStopSeconds; StartWithWindows = d.StartWithWindows;
      OverlayOffsetX = d.OverlayOffsetX; OverlayOffsetY = d.OverlayOffsetY;
      HasCustomPos = false; OverlayPosX = 0; OverlayPosY = 0;
      BridgePort = d.BridgePort; FfmpegPath = d.FfmpegPath;
      Hotkeys = new Hotkeys();
      Save();
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Effective gain for the system-audio leg: 0 when muted or off.</summary>
    public float EffectiveSystemGain {
      get { return (!SystemAudioEnabled || SystemAudioMuted) ? 0f : (float)SystemAudioGain; }
    }

    public float EffectiveMicGain {
      get { return (!MicEnabled || MicMuted) ? 0f : (float)MicGain; }
    }

    public bool WantsAnyAudio {
      get { return (SystemAudioEnabled && !SystemAudioMuted) || (MicEnabled && !MicMuted); }
    }

    /// <summary>Fill {date}/{time}/{y} and strip anything illegal in a filename.</summary>
    public string BuildFilename(string extension) {
      DateTime d = DateTime.Now;
      string pattern = string.IsNullOrEmpty(FilenamePattern) ? "Recording {date} {time}" : FilenamePattern;
      string name = pattern
        .Replace("{date}", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        .Replace("{time}", d.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
        .Replace("{y}", d.Year.ToString(CultureInfo.InvariantCulture));

      var sb = new System.Text.StringBuilder(name.Length);
      foreach (char c in name) sb.Append(c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c);
      name = sb.ToString().Trim();
      if (name.Length == 0) name = "Recording";
      return name + "." + (string.IsNullOrEmpty(extension) ? "mp4" : extension);
    }

    /// <summary>Append " (2)", " (3)"… rather than overwrite an existing take.</summary>
    public static string UniquePath(string dir, string filename) {
      string candidate = Path.Combine(dir, filename);
      if (!File.Exists(candidate)) return candidate;

      string ext = Path.GetExtension(filename);
      string stem = filename.Substring(0, filename.Length - ext.Length);
      for (int i = 2; i < 10000; i++) {
        candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
        if (!File.Exists(candidate)) return candidate;
      }
      return Path.Combine(dir, stem + " " + DateTime.Now.Ticks + ext);
    }
  }
}
