using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LightRecorder {

  internal class TabInfo {
    public int Id;
    public string Title = "";
    public string Url = "";
    public string FavIconUrl;
    public bool Audible;
    public bool Muted;
    public bool Active;
  }

  /// <summary>
  /// Local bridge to the browser extension.
  ///
  /// Bound to 127.0.0.1 only, and every connection must present the shared
  /// token written to bridge-token.txt, which the user pastes into the
  /// extension once. Without that, any web page could open a socket to us.
  ///
  /// Why it exists at all: Windows cannot separate one browser tab's audio
  /// from another's - they all share the browser's audio process. Only the
  /// browser itself can do that, so tab enumeration, per-tab muting and
  /// tab-scoped capture live here.
  ///
  /// The wire protocol is byte-for-byte the one the Electron build spoke, so
  /// the extension is unchanged and an existing pairing keeps working.
  /// </summary>
  internal class Bridge {

    readonly WebSocketServer _server = new WebSocketServer();
    readonly object _gate = new object();

    WebSocketConnection _control;
    readonly List<WebSocketConnection> _dataClients = new List<WebSocketConnection>();
    readonly Dictionary<WebSocketConnection, Timer> _authTimers = new Dictionary<WebSocketConnection, Timer>();

    Timer _heartbeat;

    public string Token { get; private set; }
    public int Port { get { return _server.Port; } }
    public List<TabInfo> Tabs = new List<TabInfo>();

    /// <summary>tabId -> the tab's mute state before we touched it, so
    /// stopping a recording puts the browser back exactly as it was.</summary>
    readonly Dictionary<int, bool> _mutedByUs = new Dictionary<int, bool>();

    public event Action<string> Connected;          // browser name
    public event Action Disconnected;
    public event Action TabsChanged;
    public event Action<string> Error;
    public event Action<byte[], int> BinaryChunk;
    public event Action<string> TabCaptureStarted;  // mime
    public event Action TabCaptureStopped;
    public event Action<string> TabCaptureError;
    public event Action<int> TabRecordingStarting;  // tabId
    /// <summary>A chunk-carrying connection went away.</summary>
    public event Action DataClosed;

    public bool IsConnected {
      get { WebSocketConnection c = _control; return c != null && c.IsOpen; }
    }

    public Bridge() {
      Token = LoadOrCreateToken();

      _server.Opened += OnOpened;
      _server.Closed += OnClosed;
      _server.TextMessage += OnText;
      _server.BinaryMessage += OnBinary;
      _server.Error += delegate (string m) {
        Action<string> h = Error;
        if (h != null) h(m);
      };
    }

    // ---------------------------------------------------------------- token

    static string LoadOrCreateToken() {
      try {
        if (File.Exists(Paths.BridgeToken)) {
          string t = File.ReadAllText(Paths.BridgeToken).Trim();
          if (t.Length >= 16) return t;
        }
      } catch (Exception) { /* first run */ }

      var bytes = new byte[16];
      using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
      var sb = new StringBuilder(32);
      foreach (byte b in bytes) sb.Append(b.ToString("x2"));
      string token = sb.ToString();

      try { File.WriteAllText(Paths.BridgeToken, token); } catch (Exception) { }
      return token;
    }

    // ------------------------------------------------------------ lifecycle

    Timer _retry;
    int _retryPort;

    public void Start(int port) {
      Stop();
      _retryPort = port;

      if (!_server.Start(port)) {
        // Something else has the port - often a previous instance of us whose
        // socket has not been released yet. Keep trying quietly rather than
        // making the user restart the app to get their extension back.
        _retry = new Timer(delegate {
          if (_server.IsListening) return;
          if (_server.Start(_retryPort)) {
            Log.Info("bridge: port " + _retryPort + " came free");
            StartHeartbeat();
            if (_retry != null) { _retry.Dispose(); _retry = null; }
          }
        }, null, 15000, 15000);
        return;
      }

      StartHeartbeat();
    }

    void StartHeartbeat() {
      if (_heartbeat != null) _heartbeat.Dispose();
      // The extension's MV3 service worker is evicted when idle; a periodic
      // message keeps it alive for as long as we are running.
      _heartbeat = new Timer(delegate {
        if (IsConnected) Send("ping", null);
      }, null, 20000, 20000);
    }

    public void Stop() {
      if (_heartbeat != null) { _heartbeat.Dispose(); _heartbeat = null; }
      if (_retry != null) { _retry.Dispose(); _retry = null; }

      lock (_gate) {
        foreach (Timer t in _authTimers.Values) t.Dispose();
        _authTimers.Clear();
        _control = null;
        _dataClients.Clear();
        Tabs = new List<TabInfo>();
      }
      _server.Stop();
    }

    // ------------------------------------------------------------ handshake

    void OnOpened(WebSocketConnection c) {
      // Anything that has not identified itself within five seconds is not the
      // extension.
      var timer = new Timer(delegate {
        if (!c.Authenticated) c.Close();
      }, null, 5000, Timeout.Infinite);
      lock (_gate) _authTimers[c] = timer;
    }

    void OnClosed(WebSocketConnection c) {
      bool wasControl = false, wasData = false;
      lock (_gate) {
        Timer t;
        if (_authTimers.TryGetValue(c, out t)) { t.Dispose(); _authTimers.Remove(c); }
        wasData = _dataClients.Remove(c);
        if (_control == c) { _control = null; Tabs = new List<TabInfo>(); wasControl = true; }
      }
      if (wasControl) Fire(Disconnected);
      if (wasData) Fire(DataClosed);
    }

    void OnBinary(WebSocketConnection c, byte[] data, int count) {
      // Tab-capture chunks. The offscreen document streams these on its own
      // socket so they never pass through the service worker.
      if (!c.Authenticated) return;
      Action<byte[], int> h = BinaryChunk;
      if (h != null) h(data, count);
    }

    void OnText(WebSocketConnection c, string text) {
      Dictionary<string, object> msg;
      try {
        msg = Json.Parse(text) as Dictionary<string, object>;
      } catch (Exception) {
        return;
      }
      if (msg == null) return;

      if (!c.Authenticated) {
        string supplied = Json.Str(msg, "token", "");
        bool ok = Json.Str(msg, "type", "") == "hello" && ConstantTimeEquals(supplied, Token);
        if (!ok) { c.Close(); return; }

        c.Authenticated = true;
        c.Role = Json.Str(msg, "role", "control") == "data" ? "data" : "control";

        lock (_gate) {
          Timer t;
          if (_authTimers.TryGetValue(c, out t)) { t.Dispose(); _authTimers.Remove(c); }
        }

        if (c.Role == "data") {
          lock (_gate) _dataClients.Add(c);
          c.SendText(Json.Write(NewMessage("welcome", new KeyValuePair<string, object>("role", "data"))));
          return;
        }

        // Only one control client at a time - a reloaded extension replaces the
        // stale worker rather than piling up.
        WebSocketConnection previous;
        lock (_gate) { previous = _control; _control = c; }
        if (previous != null && previous != c) previous.Close();

        c.SendText(Json.Write(NewMessage("welcome", new KeyValuePair<string, object>("app", "Light Recorder"))));
        Send("getTabs", null);

        Action<string> h = Connected;
        if (h != null) h(Json.Str(msg, "browser", "browser"));
        return;
      }

      if (c.Role == "control") Handle(msg);
    }

    static bool ConstantTimeEquals(string a, string b) {
      if (a == null || b == null || a.Length != b.Length) return false;
      int diff = 0;
      for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
      return diff == 0;
    }

    // ------------------------------------------------------------- protocol

    void Handle(Dictionary<string, object> msg) {
      switch (Json.Str(msg, "type", "")) {
        case "tabs": {
          var list = new List<TabInfo>();
          var arr = Json.List(msg, "tabs");
          if (arr != null) {
            foreach (object o in arr) {
              var t = o as Dictionary<string, object>;
              if (t == null) continue;
              var info = new TabInfo();
              info.Id = Json.Int(t, "id", -1);
              info.Title = Json.Str(t, "title", "");
              info.Url = Json.Str(t, "url", "");
              object fav = Json.Get(t, "favIconUrl");
              info.FavIconUrl = fav as string;
              info.Audible = Json.Bool(t, "audible", false);
              info.Muted = Json.Bool(t, "muted", false);
              info.Active = Json.Bool(t, "active", false);
              if (info.Id >= 0) list.Add(info);
            }
          }
          Tabs = list;
          Fire(TabsChanged);
          break;
        }

        case "tabCaptureStarted": {
          Action<string> h = TabCaptureStarted;
          if (h != null) h(Json.Str(msg, "mime", ""));
          break;
        }

        case "tabCaptureStopped":
          Fire(TabCaptureStopped);
          break;

        case "tabCaptureError": {
          Action<string> h = TabCaptureError;
          if (h != null) h(Json.Str(msg, "error", "Tab capture failed"));
          break;
        }

        case "tabRecordingStarting": {
          // The user pressed "Record this tab" inside the extension popup, so
          // the app has to open the output file rather than initiate capture.
          Action<int> h = TabRecordingStarting;
          if (h != null) h(Json.Int(msg, "tabId", -1));
          break;
        }

        case "pong":
          break;
      }
    }

    static void Fire(Action h) { if (h != null) h(); }

    static Dictionary<string, object> NewMessage(string type, params KeyValuePair<string, object>[] fields) {
      var m = new Dictionary<string, object>();
      m["type"] = type;
      foreach (KeyValuePair<string, object> kv in fields) m[kv.Key] = kv.Value;
      return m;
    }

    bool Send(string type, Dictionary<string, object> extra) {
      WebSocketConnection c = _control;
      if (c == null || !c.IsOpen) return false;

      var m = new Dictionary<string, object>();
      m["type"] = type;
      if (extra != null) foreach (KeyValuePair<string, object> kv in extra) m[kv.Key] = kv.Value;

      try { return c.SendText(Json.Write(m)); }
      catch (Exception) { return false; }
    }

    // -------------------------------------------------------------- actions

    public bool RefreshTabs() { return Send("getTabs", null); }

    /// <summary>
    /// Mute or unmute one tab, remembering its original state so stopping a
    /// recording restores the browser exactly as the user left it.
    /// </summary>
    public bool SetTabMuted(int tabId, bool muted) {
      TabInfo tab = FindTab(tabId);
      if (tab != null && !_mutedByUs.ContainsKey(tabId)) _mutedByUs[tabId] = tab.Muted;
      if (tab != null) tab.Muted = muted;

      var extra = new Dictionary<string, object>();
      extra["tabId"] = tabId;
      extra["muted"] = muted;
      return Send("setMuted", extra);
    }

    public void RestoreTabMutes() {
      if (_mutedByUs.Count == 0) return;
      var snapshot = new List<KeyValuePair<int, bool>>(_mutedByUs);
      _mutedByUs.Clear();

      foreach (KeyValuePair<int, bool> kv in snapshot) {
        var extra = new Dictionary<string, object>();
        extra["tabId"] = kv.Key;
        extra["muted"] = kv.Value;
        Send("setMuted", extra);
        TabInfo tab = FindTab(kv.Key);
        if (tab != null) tab.Muted = kv.Value;
      }
    }

    public bool StartTabCapture(int tabId, Settings s) {
      var opts = new Dictionary<string, object>();
      opts["fps"] = s.Fps;
      opts["resolution"] = s.Resolution;
      opts["bitrateMbps"] = s.BitrateMbps;
      opts["micEnabled"] = s.MicEnabled && !s.MicMuted;
      opts["tabAudioEnabled"] = s.SystemAudioEnabled && !s.SystemAudioMuted;

      var extra = new Dictionary<string, object>();
      extra["tabId"] = tabId;
      extra["opts"] = opts;
      return Send("startTabCapture", extra);
    }

    public bool StopTabCapture() { return Send("stopTabCapture", null); }

    /// <summary>MediaRecorder pauses natively and leaves the paused time out
    /// of its timestamps, so a tab recording pauses in the browser.</summary>
    public bool PauseTabCapture() { return Send("pauseTabCapture", null); }
    public bool ResumeTabCapture() { return Send("resumeTabCapture", null); }

    public TabInfo FindTab(int tabId) {
      List<TabInfo> tabs = Tabs;
      foreach (TabInfo t in tabs) if (t.Id == tabId) return t;
      return null;
    }
  }
}
