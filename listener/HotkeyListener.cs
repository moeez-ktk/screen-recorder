// Light Recorder - the hotkey listener.
//
// The only thing that stays resident. It owns the shortcut that brings the
// recorder up and does nothing else: no window, no tray icon, no timers. It
// sits blocked in GetMessage until the OS posts a WM_HOTKEY, which costs no CPU
// and a working set of about a megabyte once the runtime's startup pages have
// been handed back.
//
// On a press it pokes the recorder if it is running - the same message a
// second launch of the exe would send - and starts it otherwise. The recorder
// quits when its overlay is put away, so the process that costs anything only
// exists while it is wanted.
//
// Deliberately avoids System.Windows.Forms: a message loop does not need it,
// and pulling it in triples the memory for nothing.
//
// Built by build.ps1 into build\LightRecorderHotkey.exe, next to the recorder.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace LightRecorder.Listener {

  static class Native {
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX {
      public int cbSize; public int style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
      public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    // The class is registered Unicode, so its default handling must be too:
    // DefWindowProcA reads the UTF-16 title WM_NCCREATE hands it as ANSI and
    // the window ends up named "L", which nothing can find.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr proc);

    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
  }

  static class Program {
    // Shared with the recorder: see Program.cs and Hotkeys.cs there.
    const string MutexName = @"Global\LightRecorderListener";
    const string WindowTitle = "LightRecorderListener";
    const string RecorderIpcTitle = "LightRecorderIPC";
    const string RecorderExe = "LightRecorder.exe";
    /// <summary>Posted by the recorder when the shortcut is changed in Settings.</summary>
    public const uint WM_APP_RELOAD = 0x8000 + 1;
    /// <summary>Index of "launch" in the recorder's command table: show the
    /// overlay, or put it away if it is up.</summary>
    const int LaunchCommand = 0;

    /// <summary>Always registered alongside the one from Settings, so there
    /// is a way in even when that one has been rebound to something odd.</summary>
    const string FixedAccelerator = "Control+Shift+R";

    static IntPtr _hwnd;
    static Native.WndProc _wndProcRef;              // must outlive the window
    static readonly List<string> _bound = new List<string>();
    static uint _recorderMessage;

    static string SettingsFile {
      get {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Light Recorder", "settings.json");
      }
    }

    static string LogFile { get { return Path.Combine(Path.GetDirectoryName(SettingsFile), "listener.log"); } }

    static void Log(string message) {
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
        // Rewritten when it grows: nobody wants a log of every keypress.
        if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 16 * 1024) File.Delete(LogFile);
        File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
      } catch (Exception) { }
    }

    static int Main() {
      bool created;
      using (var mutex = new Mutex(true, MutexName, out created)) {
        if (!created) return 0;                       // one is enough
        Run();
        GC.KeepAlive(mutex);
      }
      return 0;
    }

    static void Run() {
      _recorderMessage = Native.RegisterWindowMessage("LightRecorderCommand");

      _wndProcRef = WndProcImpl;
      var wc = new Native.WNDCLASSEX();
      wc.cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX));
      wc.lpfnWndProc = _wndProcRef;
      wc.hInstance = Native.GetModuleHandle(null);
      wc.lpszClassName = "LightRecorderListenerWnd";
      if (Native.RegisterClassEx(ref wc) == 0) { Log("RegisterClassEx failed: " + Marshal.GetLastWin32Error()); return; }

      _hwnd = Native.CreateWindowEx(0, wc.lpszClassName, WindowTitle, 0, 0, 0, 0, 0,
                                    Native.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
      if (_hwnd == IntPtr.Zero) { Log("CreateWindowEx failed: " + Marshal.GetLastWin32Error()); return; }

      Log("listener starting (pid " + Process.GetCurrentProcess().Id + ")");
      RegisterAll();

      // Startup is the only time this process touches much memory. Hand it
      // back and stay small for the rest of the session.
      try { Native.EmptyWorkingSet(Native.GetCurrentProcess()); } catch (Exception) { }

      Native.MSG m;
      while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) Native.DispatchMessage(ref m);
    }

    // ------------------------------------------------------------- hotkeys

    static void RegisterAll() {
      for (int i = 0; i < _bound.Count; i++) Native.UnregisterHotKey(_hwnd, i);
      _bound.Clear();

      var wanted = new List<string>();
      string fromSettings = ReadToggleAccelerator();
      if (!string.IsNullOrEmpty(fromSettings)) wanted.Add(fromSettings);
      if (!wanted.Contains(FixedAccelerator)) wanted.Add(FixedAccelerator);

      var ok = new List<string>();
      var taken = new List<string>();
      foreach (string accel in wanted) {
        uint mods, vk;
        if (!Parse(accel, out mods, out vk)) { taken.Add(accel + " (unreadable)"); continue; }
        int id = _bound.Count;
        _bound.Add(accel);
        if (Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) ok.Add(accel);
        else taken.Add(accel);
      }
      Log("registered: " + (ok.Count > 0 ? string.Join(", ", ok.ToArray()) : "none") +
          (taken.Count > 0 ? "; already owned by another application: " + string.Join(", ", taken.ToArray()) : ""));
    }

    /// <summary>The show/hide shortcut from the recorder's settings, without
    /// a JSON parser: it is one string in a file the recorder writes.</summary>
    static string ReadToggleAccelerator() {
      try {
        if (!File.Exists(SettingsFile)) return "Control+Shift+D";
        Match m = Regex.Match(File.ReadAllText(SettingsFile), "\"toggleOverlay\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "Control+Shift+D";
      } catch (Exception) {
        return "Control+Shift+D";
      }
    }

    static bool Parse(string accel, out uint mods, out uint vk) {
      mods = 0; vk = 0;
      foreach (string raw in accel.Split('+')) {
        string p = raw.Trim().ToLowerInvariant();
        if (p.Length == 0) continue;
        switch (p) {
          case "control": case "ctrl": mods |= Native.MOD_CONTROL; continue;
          case "alt": mods |= Native.MOD_ALT; continue;
          case "shift": mods |= Native.MOD_SHIFT; continue;
          case "super": case "win": case "meta": mods |= Native.MOD_WIN; continue;
        }
        if (p.Length == 1 && ((p[0] >= 'a' && p[0] <= 'z') || (p[0] >= '0' && p[0] <= '9'))) { vk = char.ToUpperInvariant(p[0]); continue; }
        if (p.Length >= 2 && p[0] == 'f') { int n; if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x6F + n); continue; } }
        switch (p) {
          case "space": vk = 0x20; continue;
          case "escape": case "esc": vk = 0x1B; continue;
          case "tab": vk = 0x09; continue;
          case "enter": case "return": vk = 0x0D; continue;
          case "home": vk = 0x24; continue;
          case "end": vk = 0x23; continue;
          case "pageup": vk = 0x21; continue;
          case "pagedown": vk = 0x22; continue;
          case "insert": vk = 0x2D; continue;
          case "delete": vk = 0x2E; continue;
          case "pause": vk = 0x13; continue;
          case "printscreen": vk = 0x2C; continue;
        }
        return false;
      }
      // Function keys may stand alone; anything else would swallow a plain key.
      return vk != 0 && (mods != 0 || (vk >= 0x70 && vk <= 0x87));
    }

    static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
      if (msg == Native.WM_HOTKEY) {
        Fire();
        // Starting a process touches a few megabytes that are never needed
        // again; give them back rather than carry them until the next press.
        try { Native.EmptyWorkingSet(Native.GetCurrentProcess()); } catch (Exception) { }
        return IntPtr.Zero;
      }
      if (msg == WM_APP_RELOAD) { RegisterAll(); return IntPtr.Zero; }
      return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------- firing

    static void Fire() {
      // Running: the same nudge a second launch would give it.
      IntPtr recorder = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, RecorderIpcTitle);
      if (recorder != IntPtr.Zero) {
        Native.PostMessage(recorder, _recorderMessage, new IntPtr(LaunchCommand), IntPtr.Zero);
        return;
      }

      string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RecorderExe);
      if (!File.Exists(exe)) { Log("recorder not found next to the listener: " + exe); return; }
      try {
        var psi = new ProcessStartInfo(exe);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetDirectoryName(exe);
        Process.Start(psi);
      } catch (Exception err) {
        Log("could not start the recorder: " + err.Message);
      }
    }
  }
}
