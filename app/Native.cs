using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace LightRecorder {

  /// <summary>
  /// The Win32 surface the app uses. Everything the Electron build got from
  /// Chromium - always-on-top panels, capture exclusion, monitor geometry,
  /// window enumeration, thumbnails, global hotkeys - comes from here instead,
  /// which is why the whole app fits in one process.
  /// </summary>
  internal static class Native {

    // ------------------------------------------------------------ constants

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_DISABLED = 0x08000000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_APP = 0x8000;
    public const uint WM_CLOSE = 0x0010;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;

    /// <summary>Keeps our own UI out of the recording. 0x11 is
    /// WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+); 0x01 is WDA_MONITOR, the
    /// older and blunter fallback that blanks the window instead.</summary>
    public const uint WDA_NONE = 0x00;
    public const uint WDA_MONITOR = 0x01;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4,
                      MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    // ------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
      public int Left, Top, Right, Bottom;
      public int Width { get { return Right - Left; } }
      public int Height { get { return Bottom - Top; } }
      public Rectangle ToRectangle() { return new Rectangle(Left, Top, Right - Left, Bottom - Top); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION {
      public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const int ULW_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX {
      public int cbSize;
      public RECT rcMonitor;
      public RECT rcWork;
      public uint dwFlags;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 0x1;
    public const uint MONITOR_DEFAULTTONEAREST = 0x2;

    // ------------------------------------------------------------- user32

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    static extern int GetWindowLong32(IntPtr hWnd, int index);

    public static long GetWindowLongSafe(IntPtr hWnd, int index) {
      if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, index).ToInt64();
      return GetWindowLong32(hWnd, index);
    }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    /// <summary>Renders layered/composited children too; without it a lot of
    /// modern windows print as a blank rectangle.</summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst,
      ref SIZE size, IntPtr hdcSrc, ref POINT src, int colorKey, ref BLENDFUNCTION blend, int flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    // ------------------------------------------------------------- dwmapi

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    /// <summary>DWMWA_CLOAKED. Store apps that are "running" but not on screen
    /// report visible through IsWindowVisible and would otherwise fill the
    /// picker with ghosts.</summary>
    public const int DWMWA_CLOAKED = 14;

    // -------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                     IntPtr src, int sx, int sy, uint rop);

    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;

    // ----------------------------------------------------------- kernel32

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

    /// <summary>Wall-clock seconds since 1970, to the microsecond - the clock
    /// ffmpeg stamps captured frames with. DateTime.UtcNow is not promised to
    /// move more often than once per timer tick.</summary>
    public static double PreciseEpochSeconds() {
      long ft;
      GetSystemTimePreciseAsFileTime(out ft);
      return (ft - 116444736000000000L) / 1e7;
    }

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    [DllImport("psapi.dll")]
    public static extern bool EmptyWorkingSet(IntPtr proc);

    /// <summary>
    /// Hand the pages we are no longer touching back to the OS.
    ///
    /// The .NET heap holds on to memory after a window closes; the pages stay
    /// committed but untouched, which is exactly what a working-set trim is
    /// for. Called after any window is destroyed and after a recording ends,
    /// so sitting in the tray really does cost single-digit megabytes.
    /// </summary>
    public static void TrimWorkingSet() {
      try {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        EmptyWorkingSet(GetCurrentProcess());
      } catch (Exception) { }
    }

    // ------------------------------------------------- child process hygiene

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    const uint HANDLE_FLAG_INHERIT = 0x00000001;

    /// <summary>
    /// Stop a handle being handed to child processes.
    ///
    /// Sockets and pipes are inheritable by default, and starting a child with
    /// redirected stdio hands it every inheritable handle we own. That is how a
    /// long-dead copy of the app can leave its bridge port bound: ffmpeg
    /// inherited the listening socket, outlived its parent, and kept the port
    /// occupied - so the next launch reported "port already in use" and the
    /// browser extension could never reconnect.
    /// </summary>
    public static void DontInherit(IntPtr handle) {
      if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
      try { SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0); } catch (Exception) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
      public long PerProcessUserTimeLimit;
      public long PerJobUserTimeLimit;
      public uint LimitFlags;
      public UIntPtr MinimumWorkingSetSize;
      public UIntPtr MaximumWorkingSetSize;
      public uint ActiveProcessLimit;
      public UIntPtr Affinity;
      public uint PriorityClass;
      public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS {
      public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
      public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
      public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
      public IO_COUNTERS IoInfo;
      public UIntPtr ProcessMemoryLimit;
      public UIntPtr JobMemoryLimit;
      public UIntPtr PeakProcessMemoryUsed;
      public UIntPtr PeakJobMemoryUsed;
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    static IntPtr _childJob;
    static bool _childJobTried;

    /// <summary>
    /// Put a child in a job that dies when we do.
    ///
    /// If the app is killed or crashes mid-recording, ffmpeg would otherwise
    /// carry on capturing the screen to disk with nothing left to stop it. The
    /// output is fragmented MP4, so what is on disk stays valid and playable -
    /// the recording costs its last fragment, which is exactly the trade the
    /// format was chosen for.
    /// </summary>
    public static void KillWithUs(IntPtr processHandle) {
      if (processHandle == IntPtr.Zero) return;
      try {
        if (!_childJobTried) {
          _childJobTried = true;
          IntPtr job = CreateJobObject(IntPtr.Zero, null);
          if (job != IntPtr.Zero) {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr block = Marshal.AllocHGlobal(size);
            try {
              Marshal.StructureToPtr(info, block, false);
              if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, block, (uint)size))
                _childJob = job;
            } finally {
              Marshal.FreeHGlobal(block);
            }
          }
        }
        if (_childJob != IntPtr.Zero) AssignProcessToJobObject(_childJob, processHandle);
      } catch (Exception) {
        // Worth having, not worth failing a recording over.
      }
    }

    // ----------------------------------------------------------------- DPI

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    /// <summary>
    /// Ask for per-monitor DPI awareness, degrading through the three
    /// generations of this API. Everything the app draws is laid out in code
    /// and scaled through Dpi.Scale, so a 4K laptop next to a 1080p monitor
    /// gets the right pill on both.
    /// </summary>
    public static void SetupDpiAwareness() {
      try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; }
      catch (Exception) { }
      try { if (SetProcessDpiAwareness(2) == 0) return; }             // PROCESS_PER_MONITOR_DPI_AWARE
      catch (Exception) { }
      try { SetProcessDPIAware(); } catch (Exception) { }
    }

    public static int DpiForWindow(IntPtr hWnd) {
      try {
        uint dpi = GetDpiForWindow(hWnd);
        if (dpi >= 48) return (int)dpi;
      } catch (Exception) { }
      try {
        uint x, y;
        IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x >= 48) return (int)x;
      } catch (Exception) { }
      return 96;
    }

    public static int DpiForPoint(Point p) {
      try {
        uint x, y;
        POINT pt; pt.X = p.X; pt.Y = p.Y;
        IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x >= 48) return (int)x;
      } catch (Exception) { }
      return 96;
    }

    // --------------------------------------------------------- convenience

    public static string WindowTitle(IntPtr hWnd) {
      int len = GetWindowTextLength(hWnd);
      if (len <= 0) return "";
      var sb = new StringBuilder(len + 1);
      GetWindowText(hWnd, sb, sb.Capacity);
      return sb.ToString();
    }

    public static string WindowClass(IntPtr hWnd) {
      var sb = new StringBuilder(256);
      GetClassName(hWnd, sb, sb.Capacity);
      return sb.ToString();
    }

    public static bool IsCloaked(IntPtr hWnd) {
      int cloaked;
      if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
      return cloaked != 0;
    }

    /// <summary>
    /// Keep this window out of the user's own recording.
    ///
    /// Timing matters: applied before the window is shown this silently does
    /// nothing on Windows, which is why every caller applies it on show rather
    /// than once at construction.
    /// </summary>
    public static void ExcludeFromCapture(IntPtr hWnd) {
      if (hWnd == IntPtr.Zero) return;
      // Escape hatch for working on the UI itself: with the affinity set, the
      // window is invisible to every screenshot tool as well as to ffmpeg,
      // which makes it impossible to look at what you just drew.
      if (Environment.GetEnvironmentVariable("LIGHTRECORDER_ALLOW_CAPTURE") == "1") return;
      if (SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE)) return;
      // Pre-2004 builds only have the blunt instrument. Better a black
      // rectangle in the recording than the overlay itself.
      SetWindowDisplayAffinity(hWnd, WDA_MONITOR);
    }

    /// <summary>Float above fullscreen games and other topmost windows.</summary>
    public static void KeepOnTop(IntPtr hWnd) {
      SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                   SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
  }
}
