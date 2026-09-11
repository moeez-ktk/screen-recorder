// Light Recorder - global hotkey listener.
//
// This is the only thing that stays resident. It owns every global hotkey and
// does nothing else: no window, no tray icon, no timers, no polling. It sits
// blocked in GetMessage until the OS posts a WM_HOTKEY, which costs no CPU and
// only a few megabytes of working set.
//
// On a hotkey it either forwards the command to a running recorder over a
// named pipe, or launches the recorder if it is not running. The recorder
// itself exits when you are done, so the heavy process only exists while it is
// actually needed.
//
// Deliberately avoids System.Windows.Forms - pulling in WinForms for a message
// loop would roughly triple the memory for no benefit.
//
// Build:  tools\build-listener.ps1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LightRecorder {

  internal static class Native {
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG {
      public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
      public uint time; public int ptX; public int ptY;
    }

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX {
      public int cbSize; public int style; public WndProc lpfnWndProc;
      public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
      public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
      public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
      int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string name);

    // Built as /target:winexe so no console flashes on every hotkey. That also
    // means the CLI flags have no console to print to unless we borrow the
    // caller's, which is what this is for.
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(int processId);
    internal const int ATTACH_PARENT_PROCESS = -1;

    internal const int WM_HOTKEY = 0x0312;
    /// Posted by the config watcher to re-register hotkeys on the loop thread.
    internal const uint WM_APP_RELOAD = 0x8000 + 1;
    internal static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    internal const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4,
                        MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
  }

  internal class Binding {
    public string Command;
    public string Accelerator;
    public uint Modifiers;
    public uint Key;
  }

  internal static class Program {
    const string PipeName = "light-recorder-cmd";
    const string MutexName = @"Global\LightRecorderHotkeyListener";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "LightRecorder";

    static string ConfigDir {
      get {
        return Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
          "Light Recorder");
      }
    }
    static string ConfigPath { get { return Path.Combine(ConfigDir, "listener.conf"); } }
    static string LogPath { get { return Path.Combine(ConfigDir, "listener.log"); } }

    /// <summary>
    /// Started at logon with no console and no window, so a failure here is
    /// otherwise completely invisible - the user just finds that the hotkey
    /// does nothing. The log is rewritten on each start and stays a few lines.
    /// </summary>
    static void Log(string message, bool append = true) {
      try {
        Directory.CreateDirectory(ConfigDir);
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
        if (append) File.AppendAllText(LogPath, line);
        else File.WriteAllText(LogPath, line);
      } catch { /* logging must never take the listener down */ }
    }

    static IntPtr _hwnd;
    static Native.WndProc _wndProcRef;   // must outlive the window or the GC eats it
    static readonly List<Binding> _bindings = new List<Binding>();
    static string _exec = "", _execArgs = "", _workDir = "";
    static FileSystemWatcher _watcher;
    static int _reloadPending;

    [STAThread]
    static int Main(string[] args) {
      if (args.Length > 0) {
        // Piped output (the app's own status check) already works; this makes
        // it visible when run straight from a terminal too.
        Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        foreach (string a in args) {
          if (a == "--install") return SetAutostart(true);
          if (a == "--uninstall") return SetAutostart(false);
          if (a == "--status") return PrintStatus();
        }
      }

      bool created;
      using (var mutex = new Mutex(true, MutexName, out created)) {
        if (!created) {
          Console.Error.WriteLine("Light Recorder listener is already running.");
          return 0;
        }
        Run();
        return 0;
      }
    }

    // ------------------------------------------------------------- config

    static void LoadConfig() {
      _bindings.Clear();
      _exec = ""; _execArgs = ""; _workDir = "";

      if (!File.Exists(ConfigPath)) return;

      foreach (string raw in File.ReadAllLines(ConfigPath)) {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        int eq = line.IndexOf('=');
        if (eq <= 0) continue;

        string key = line.Substring(0, eq).Trim();
        string val = line.Substring(eq + 1).Trim();
        if (val.Length == 0) continue;

        if (key == "exec") { _exec = val; continue; }
        if (key == "args") { _execArgs = val; continue; }
        if (key == "cwd") { _workDir = val; continue; }

        uint mods, vk;
        if (ParseAccelerator(val, out mods, out vk)) {
          _bindings.Add(new Binding { Command = key, Accelerator = val, Modifiers = mods, Key = vk });
        }
      }
    }

    /// "Control+Alt+R" -> RegisterHotKey modifiers + virtual key code.
    static bool ParseAccelerator(string accel, out uint mods, out uint vk) {
      mods = 0; vk = 0;
      string[] parts = accel.Split('+');
      if (parts.Length == 0) return false;

      for (int i = 0; i < parts.Length; i++) {
        string p = parts[i].Trim();
        if (p.Length == 0) return false;
        string lower = p.ToLowerInvariant();

        if (lower == "control" || lower == "ctrl" || lower == "commandorcontrol" || lower == "cmdorctrl") { mods |= Native.MOD_CONTROL; continue; }
        if (lower == "alt" || lower == "option") { mods |= Native.MOD_ALT; continue; }
        if (lower == "shift") { mods |= Native.MOD_SHIFT; continue; }
        if (lower == "super" || lower == "meta" || lower == "win" || lower == "command") { mods |= Native.MOD_WIN; continue; }

        // Whatever is left has to be the key itself.
        if (p.Length == 1) {
          char c = char.ToUpperInvariant(p[0]);
          if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; continue; }
          return false;
        }
        if (lower.Length >= 2 && lower[0] == 'f') {
          int n;
          if (int.TryParse(lower.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); continue; }
        }
        switch (lower) {
          case "space": vk = 0x20; continue;
          case "tab": vk = 0x09; continue;
          case "escape": case "esc": vk = 0x1B; continue;
          case "enter": case "return": vk = 0x0D; continue;
          case "backspace": vk = 0x08; continue;
          case "delete": vk = 0x2E; continue;
          case "insert": vk = 0x2D; continue;
          case "home": vk = 0x24; continue;
          case "end": vk = 0x23; continue;
          case "pageup": vk = 0x21; continue;
          case "pagedown": vk = 0x22; continue;
          case "up": vk = 0x26; continue;
          case "down": vk = 0x28; continue;
          case "left": vk = 0x25; continue;
          case "right": vk = 0x27; continue;
          case "printscreen": vk = 0x2C; continue;
          case "pause": vk = 0x13; continue;
          default: return false;
        }
      }
      // A modifier-free hotkey would swallow that key system-wide.
      return vk != 0 && mods != 0;
    }

    // ---------------------------------------------------------- hotkeys

    static void RegisterAll() {
      for (int i = 0; i < _bindings.Count; i++) {
        Native.UnregisterHotKey(_hwnd, i);
      }
      LoadConfig();

      if (_bindings.Count == 0) {
        Log("no hotkeys configured - is " + ConfigPath + " present? Run the app once to write it.");
        return;
      }

      var taken = new List<string>();
      var ok = new List<string>();
      for (int i = 0; i < _bindings.Count; i++) {
        Binding b = _bindings[i];
        if (Native.RegisterHotKey(_hwnd, i, b.Modifiers | Native.MOD_NOREPEAT, b.Key)) {
          ok.Add(b.Accelerator + "=" + b.Command);
        } else {
          taken.Add(b.Accelerator + " (" + b.Command + ")");
        }
      }

      Log("registered: " + (ok.Count > 0 ? string.Join(", ", ok.ToArray()) : "none"));
      if (taken.Count > 0) {
        string msg = "ALREADY IN USE by another application, so these will not fire: "
                   + string.Join(", ", taken.ToArray())
                   + " - pick different keys in Settings > Shortcuts.";
        Log(msg);
        Console.Error.WriteLine(msg);
      }
      if (_exec.Length == 0) {
        Log("WARNING: no exec configured, so hotkeys cannot launch the recorder.");
      } else if (!File.Exists(_exec)) {
        // Catches a moved project or a wiped node_modules, which would
        // otherwise look like the hotkey silently doing nothing.
        Log("WARNING: exec does not exist: " + _exec + " - reinstall or run the app once to refresh " + ConfigPath);
      }
    }

    static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
      if (msg == Native.WM_HOTKEY) {
        int id = wParam.ToInt32();
        if (id >= 0 && id < _bindings.Count) Dispatch(_bindings[id].Command);
        return IntPtr.Zero;
      }
      if (msg == Native.WM_APP_RELOAD) {
        // Must happen here: RegisterHotKey binds to the calling thread, so it
        // has to run on the thread that owns this window and its message loop.
        Interlocked.Exchange(ref _reloadPending, 0);
        RegisterAll();
        return IntPtr.Zero;
      }
      return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    static void Run() {
      Log("listener starting (pid " + Process.GetCurrentProcess().Id + ")", false);

      _wndProcRef = WndProcImpl;
      var wc = new Native.WNDCLASSEX {
        cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
        lpfnWndProc = _wndProcRef,
        hInstance = Native.GetModuleHandle(null),
        lpszClassName = "LightRecorderHotkeyWnd"
      };
      if (Native.RegisterClassEx(ref wc) == 0) {
        Console.Error.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        return;
      }

      // HWND_MESSAGE: a message-only window. Never rendered, never in Alt+Tab.
      _hwnd = Native.CreateWindowEx(0, "LightRecorderHotkeyWnd", "LightRecorder", 0,
        0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
      if (_hwnd == IntPtr.Zero) {
        Console.Error.WriteLine("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        return;
      }

      RegisterAll();
      WatchConfig();

      // Blocks here. No CPU until the OS posts a message.
      Native.MSG m;
      while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) {
        Native.TranslateMessage(ref m);
        Native.DispatchMessage(ref m);
      }
    }

    /// Settings changes rewrite the config; pick them up without a restart.
    static void WatchConfig() {
      try {
        Directory.CreateDirectory(ConfigDir);
        _watcher = new FileSystemWatcher(ConfigDir, "listener.conf");
        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
        FileSystemEventHandler onChange = (s, e) => {
          // Runs on a watcher thread, so all it may do is wake the loop.
          // Editors often emit several events per save; collapse them.
          if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
          Native.PostMessage(_hwnd, Native.WM_APP_RELOAD, IntPtr.Zero, IntPtr.Zero);
        };
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.EnableRaisingEvents = true;
      } catch { /* live reload is a convenience, not a requirement */ }
    }

    // --------------------------------------------------------- dispatch

    static void Dispatch(string command) {
      if (SendToApp(command)) return;
      LaunchApp(command);
    }

    /// Returns false when the recorder is not running.
    static bool SendToApp(string command) {
      try {
        using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out)) {
          pipe.Connect(250);
          byte[] data = Encoding.UTF8.GetBytes(command + "\n");
          pipe.Write(data, 0, data.Length);
          pipe.Flush();
          return true;
        }
      } catch {
        return false;
      }
    }

    static void LaunchApp(string command) {
      if (_exec.Length == 0) {
        Console.Error.WriteLine("No exec configured in " + ConfigPath);
        return;
      }
      try {
        var psi = new ProcessStartInfo();
        string args = _execArgs + " --cmd=" + command;

        // .cmd/.bat need a shell; a real .exe is started directly.
        if (_exec.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            _exec.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) {
          psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
          psi.Arguments = "/c \"\"" + _exec + "\" " + args + "\"";
        } else {
          psi.FileName = _exec;
          psi.Arguments = args;
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (_workDir.Length > 0 && Directory.Exists(_workDir)) psi.WorkingDirectory = _workDir;
        Process.Start(psi);
      } catch (Exception ex) {
        Log("launch failed: " + ex.Message + "  (exec=" + _exec + ")");
        Console.Error.WriteLine("Launch failed: " + ex.Message);
      }
    }

    // -------------------------------------------------------- autostart

    static int SetAutostart(bool enable) {
      try {
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)) {
          if (key == null) { Console.Error.WriteLine("Cannot open Run key"); return 1; }
          if (enable) {
            key.SetValue(RunValue, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
            Console.WriteLine("Autostart enabled.");
          } else {
            key.DeleteValue(RunValue, false);
            Console.WriteLine("Autostart disabled.");
          }
        }
        return 0;
      } catch (Exception ex) {
        Console.Error.WriteLine(ex.Message);
        return 1;
      }
    }

    static int PrintStatus() {
      LoadConfig();
      Console.WriteLine("config : " + ConfigPath + (File.Exists(ConfigPath) ? "" : "  (missing)"));
      Console.WriteLine("exec   : " + (_exec.Length > 0 ? _exec + " " + _execArgs : "(not set)"));
      foreach (Binding b in _bindings) Console.WriteLine("hotkey : " + b.Accelerator + "  -> " + b.Command);

      bool autostart = false;
      try {
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) {
          autostart = key != null && key.GetValue(RunValue) != null;
        }
      } catch { }
      Console.WriteLine("autostart : " + (autostart ? "on" : "off"));

      bool running = false;
      try {
        using (var m = new Mutex(false, MutexName)) { running = !m.WaitOne(0); if (!running) m.ReleaseMutex(); }
      } catch { running = true; }
      Console.WriteLine("listener  : " + (running ? "running" : "not running"));
      Console.WriteLine("recorder  : " + (SendToApp("ping") ? "running" : "not running"));
      return 0;
    }
  }
}
