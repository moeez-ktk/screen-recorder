using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LightRecorder {

  /// <summary>What can be recorded: a monitor, an application window, or a
  /// browser tab when the extension is connected.</summary>
  internal class Source {
    public string Kind = "screen";        // screen | window | tab
    public string Id = "";
    public string Name = "";

    // screen
    public int OutputIdx;
    public int Width, Height;
    public Rectangle Bounds;
    public bool Primary;

    // window
    public IntPtr Handle = IntPtr.Zero;
    public string Title = "";
    /// <summary>Where the window sits on its monitor, relative to that
    /// monitor's top-left, as ddagrab wants it. Set by
    /// Sources.ResolveWindow when a recording starts.</summary>
    public Rectangle Region;

    // tab
    public int TabId = -1;

    // picker decoration, only populated while the picker is open
    public Image Thumbnail;
    public Image Icon;
    public bool Audible;
    public string Url;

    public string Label {
      get {
        if (!string.IsNullOrEmpty(Name)) return Name;
        if (Kind == "tab") return "Browser tab";
        if (Kind == "window") return "Window";
        return "Entire Screen";
      }
    }

    public void DisposeImages() {
      if (Thumbnail != null) { Thumbnail.Dispose(); Thumbnail = null; }
      if (Icon != null) { Icon.Dispose(); Icon = null; }
    }
  }

  /// <summary>
  /// Enumerates monitors and application windows.
  ///
  /// Thumbnails are the single most expensive thing this app ever does, and
  /// they are only produced while the picker is actually open - never during
  /// a recording, and never on a timer.
  /// </summary>
  internal static class Sources {

    /// <summary>Windows that are technically visible but are never what a user
    /// means by "record that window".</summary>
    static readonly Regex[] Noise = {
      new Regex(@"^Program Manager$", RegexOptions.IgnoreCase),
      new Regex(@"^Windows Input Experience$", RegexOptions.IgnoreCase),
      new Regex(@"^Windows Shell Experience Host$", RegexOptions.IgnoreCase),
      new Regex(@"^Microsoft Text Input Application$", RegexOptions.IgnoreCase),
      new Regex(@"^Task Switching$", RegexOptions.IgnoreCase),
      new Regex(@"^Light Recorder$", RegexOptions.IgnoreCase),
      new Regex(@"^Search$", RegexOptions.IgnoreCase),
      new Regex(@"^Start$", RegexOptions.IgnoreCase)
    };

    static readonly string[] NoiseClasses = {
      "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow",
      "ApplicationFrameWindow_Ghost", "TaskListThumbnailWnd", "ForegroundStaging"
    };

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    const uint GW_OWNER = 4;

    // ------------------------------------------------------------- monitors

    public static List<Source> Screens(bool withThumbnails) {
      var list = new List<Source>();
      Dictionary<string, int> outputs = Dxgi.OutputIndexByDeviceName();

      var handles = new List<IntPtr>();
      Native.MonitorEnumProc cb = delegate (IntPtr mon, IntPtr hdc, ref Native.RECT r, IntPtr data) {
        handles.Add(mon);
        return true;
      };
      Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

      int fallbackIndex = 0;
      foreach (IntPtr mon in handles) {
        var info = new Native.MONITORINFOEX();
        info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
        if (!Native.GetMonitorInfo(mon, ref info)) { fallbackIndex++; continue; }

        var s = new Source();
        s.Kind = "screen";
        s.Bounds = info.rcMonitor.ToRectangle();
        s.Width = s.Bounds.Width;
        s.Height = s.Bounds.Height;
        s.Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
        s.Id = "screen:" + (info.szDevice ?? ("m" + fallbackIndex));

        int idx;
        s.OutputIdx = outputs.TryGetValue(info.szDevice ?? "", out idx) ? idx : fallbackIndex;

        // Matches what the picker used to say: one display is "Entire Screen",
        // several are numbered the way Windows' own display settings number
        // them.
        string friendly = handles.Count > 1
          ? FriendlyMonitorName(info.szDevice, fallbackIndex)
          : "Entire Screen";
        s.Name = s.Primary ? friendly + " (Primary)" : friendly;

        if (withThumbnails) s.Thumbnail = CaptureScreen(s.Bounds);

        list.Add(s);
        fallbackIndex++;
      }

      // Primary first, matching the old picker's ordering.
      list.Sort(delegate (Source a, Source b) {
        if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
      });
      return list;
    }

    static string FriendlyMonitorName(string device, int index) {
      // "\\.\DISPLAY2" -> "Screen 2", which is what the user sees in Windows'
      // own display settings.
      if (!string.IsNullOrEmpty(device)) {
        int at = device.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) {
          string tail = device.Substring(at + 7).Trim('\\');
          int n;
          if (int.TryParse(tail, out n)) return "Screen " + n;
        }
      }
      return "Screen " + (index + 1);
    }

    /// <summary>The zero-config default: the whole primary monitor.</summary>
    public static Source DefaultSource() {
      List<Source> screens = Screens(false);
      foreach (Source s in screens) if (s.Primary) return s;
      if (screens.Count > 0) return screens[0];

      var fallback = new Source();
      fallback.Kind = "screen";
      fallback.Id = "screen:primary";
      fallback.Name = "Entire Screen (Primary)";
      return fallback;
    }

    // -------------------------------------------------------------- windows

    public static List<Source> Windows(bool withThumbnails) {
      var list = new List<Source>();
      uint self = (uint)Process.GetCurrentProcess().Id;

      Native.EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr param) {
        try {
          if (!Native.IsWindowVisible(hWnd)) return true;
          if (Native.IsCloaked(hWnd)) return true;                 // "running" store apps
          if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true; // dialogs and popups

          long ex = Native.GetWindowLongSafe(hWnd, Native.GWL_EXSTYLE);
          if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

          uint pid;
          Native.GetWindowThreadProcessId(hWnd, out pid);
          if (pid == self) return true;                             // our own overlay

          string title = Native.WindowTitle(hWnd);
          if (string.IsNullOrEmpty(title.Trim())) return true;
          foreach (Regex rx in Noise) if (rx.IsMatch(title)) return true;

          string cls = Native.WindowClass(hWnd);
          foreach (string bad in NoiseClasses)
            if (string.Equals(cls, bad, StringComparison.OrdinalIgnoreCase)) return true;

          Native.RECT r;
          if (!Native.GetWindowRect(hWnd, out r) || r.Width < 64 || r.Height < 64) return true;

          var s = new Source();
          s.Kind = "window";
          s.Handle = hWnd;
          s.Id = "window:" + hWnd.ToInt64();
          s.Name = title;
          s.Title = title;
          s.Width = r.Width;
          s.Height = r.Height;
          s.Bounds = r.ToRectangle();

          if (withThumbnails) {
            s.Thumbnail = CaptureWindow(hWnd, r);
            s.Icon = WindowIcon(hWnd, pid);
          }
          list.Add(s);
        } catch (Exception) {
          // A window that vanishes mid-enumeration is normal.
        }
        return true;
      };

      Native.EnumWindows(cb, IntPtr.Zero);
      list.Sort(delegate (Source a, Source b) {
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
      });
      return list;
    }

    /// <summary>
    /// Work out where a window is right now, so a recording captures that
    /// part of the screen.
    ///
    /// A window is recorded as a region of its monitor through ddagrab, not
    /// by asking GDI for the window's own pixels. GDI capture of anything
    /// hardware-accelerated - every browser, every game, most of what people
    /// record - comes back solid black, and it found the window by title, so
    /// a browser whose title changes with the active tab could not be started
    /// at all. The region is what the user sees; a window dragged over it
    /// gets recorded too, which is the trade.
    ///
    /// Called when recording starts, not when the window was picked: the
    /// window has usually moved since, and it may be gone.
    /// </summary>
    public static bool ResolveWindow(Source s, out string error) {
      error = null;
      if (s.Handle == IntPtr.Zero || !Native.IsWindow(s.Handle)) {
        error = "That window has been closed. Pick another source.";
        return false;
      }
      if (Native.IsIconic(s.Handle)) {
        error = "That window is minimised, so there is nothing on screen to record. Restore it first.";
        return false;
      }

      Rectangle frame = Native.WindowFrame(s.Handle);
      IntPtr mon = Native.MonitorFromWindow(s.Handle, Native.MONITOR_DEFAULTTONEAREST);
      var info = new Native.MONITORINFOEX();
      info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
      if (frame.Width <= 0 || frame.Height <= 0 || !Native.GetMonitorInfo(mon, ref info)) {
        error = "Could not find that window on any screen.";
        return false;
      }

      // Only what is actually on the monitor can be captured, and H.264 wants
      // even dimensions.
      Rectangle monitor = info.rcMonitor.ToRectangle();
      Rectangle visible = Rectangle.Intersect(frame, monitor);
      visible.Width &= ~1;
      visible.Height &= ~1;
      if (visible.Width < 2 || visible.Height < 2) {
        error = "That window is off screen. Move it onto a display first.";
        return false;
      }

      int idx;
      Dictionary<string, int> outputs = Dxgi.OutputIndexByDeviceName();
      s.OutputIdx = outputs.TryGetValue(info.szDevice ?? "", out idx) ? idx : 0;
      s.Region = new Rectangle(visible.X - monitor.X, visible.Y - monitor.Y, visible.Width, visible.Height);
      s.Bounds = visible;
      s.Width = visible.Width;
      s.Height = visible.Height;
      s.Title = Native.WindowTitle(s.Handle);
      if (!string.IsNullOrEmpty(s.Title)) s.Name = s.Title;    // the overlay shows what is being recorded now
      return true;
    }

    // ----------------------------------------------------------- thumbnails

    const int ThumbW = 320, ThumbH = 180;

    /// <summary>
    /// BitBlt straight off the screen DC. One copy, scaled on the way into a
    /// small bitmap, and only while the picker is open.
    /// </summary>
    static Image CaptureScreen(Rectangle bounds) {
      if (bounds.Width <= 0 || bounds.Height <= 0) return null;
      IntPtr screenDc = IntPtr.Zero;
      try {
        screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        using (var full = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(full)) {
          IntPtr dst = g.GetHdc();
          try {
            // CAPTUREBLT includes layered windows, which is what the user sees.
            Native.BitBlt(dst, 0, 0, bounds.Width, bounds.Height,
                          screenDc, bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT);
          } finally {
            g.ReleaseHdc(dst);
          }
          return Downscale(full);
        }
      } catch (Exception) {
        return null;
      } finally {
        if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
      }
    }

    /// <summary>
    /// PrintWindow with PW_RENDERFULLCONTENT, which is the only variant that
    /// works for the composited and hardware-accelerated windows most modern
    /// apps use; without it half the list comes back blank.
    /// </summary>
    static Image CaptureWindow(IntPtr hWnd, Native.RECT r) {
      if (r.Width <= 0 || r.Height <= 0) return null;
      if (Native.IsIconic(hWnd)) return null;                 // minimised has nothing to show

      try {
        using (var full = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(full)) {
          IntPtr dc = g.GetHdc();
          bool ok;
          try { ok = Native.PrintWindow(hWnd, dc, Native.PW_RENDERFULLCONTENT); }
          finally { g.ReleaseHdc(dc); }
          if (!ok) return null;
          return Downscale(full);
        }
      } catch (Exception) {
        return null;
      }
    }

    static Image Downscale(Bitmap full) {
      double scale = Math.Min((double)ThumbW / full.Width, (double)ThumbH / full.Height);
      if (scale > 1) scale = 1;
      int w = Math.Max(1, (int)(full.Width * scale));
      int h = Math.Max(1, (int)(full.Height * scale));

      var thumb = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
      using (Graphics g = Graphics.FromImage(thumb)) {
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(full, 0, 0, w, h);
      }
      return thumb;
    }

    // ----------------------------------------------------------------- icons

    const uint WM_GETICON = 0x007F;
    const int GCLP_HICONSM = -34;
    const int GCLP_HICON = -14;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLong")]
    static extern uint GetClassLong32(IntPtr hWnd, int index);

    static IntPtr ClassIcon(IntPtr hWnd, int index) {
      if (IntPtr.Size == 8) return GetClassLongPtr64(hWnd, index);
      return new IntPtr(GetClassLong32(hWnd, index));
    }

    static Image WindowIcon(IntPtr hWnd, uint pid) {
      // ICON_SMALL2 (2) is the one the shell itself asks for and the one apps
      // are most reliable about answering.
      IntPtr h = Native.SendMessage(hWnd, WM_GETICON, new IntPtr(2), IntPtr.Zero);
      if (h == IntPtr.Zero) h = Native.SendMessage(hWnd, WM_GETICON, IntPtr.Zero, IntPtr.Zero);
      if (h == IntPtr.Zero) h = Native.SendMessage(hWnd, WM_GETICON, new IntPtr(1), IntPtr.Zero);
      if (h == IntPtr.Zero) h = ClassIcon(hWnd, GCLP_HICONSM);
      if (h == IntPtr.Zero) h = ClassIcon(hWnd, GCLP_HICON);

      if (h != IntPtr.Zero) {
        try {
          using (Icon icon = Icon.FromHandle(h)) return icon.ToBitmap();
        } catch (Exception) { }
      }

      // Last resort: the executable's own icon.
      try {
        Process p = Process.GetProcessById((int)pid);
        string exe = p.MainModule.FileName;
        using (Icon icon = Icon.ExtractAssociatedIcon(exe)) {
          if (icon != null) return icon.ToBitmap();
        }
      } catch (Exception) { }

      return null;
    }
  }
}
