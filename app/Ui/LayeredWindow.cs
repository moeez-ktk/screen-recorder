using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>A clickable region inside a layered window. These windows have
  /// no child controls, so hit testing is a list of rectangles.</summary>
  internal class Hit {
    public string Id;
    public Rectangle Bounds;
    public bool Enabled = true;
    public string Tooltip;
  }

  /// <summary>
  /// A window with a real per-pixel alpha channel, drawn entirely by hand.
  ///
  /// This is what makes the overlay look the way it did under Chromium: an
  /// antialiased rounded pill with a soft drop shadow over whatever is behind
  /// it. A colour-keyed or region-clipped window cannot do that - the corners
  /// come out jagged and the shadow is impossible. UpdateLayeredWindow takes a
  /// 32-bit premultiplied bitmap and composites it properly.
  ///
  /// The cost is that there are no child controls and no WM_PAINT, so hover,
  /// pressing and clicking are handled against a list of rectangles. For a
  /// row of icon buttons that is less code than owner-drawing would have been.
  /// </summary>
  internal class LayeredWindow : Form {

    protected readonly Dpi Dpi = new Dpi();
    protected readonly List<Hit> Hits = new List<Hit>();

    protected string Hovered;
    protected string Pressed;

    /// <summary>Set false for panels that should take focus when clicked.</summary>
    protected bool NoActivate = true;

    /// <summary>Region that behaves like a title bar for dragging.</summary>
    protected Rectangle DragRegion = Rectangle.Empty;

    ToolTip _tip;
    Timer _tipTimer;
    string _tipFor;
    bool _shown;

    public LayeredWindow() {
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      MinimizeBox = false;
      MaximizeBox = false;
      TopMost = true;
      // Nothing is ever painted through WM_PAINT, so double buffering and the
      // background brush would only be wasted work.
      SetStyle(ControlStyles.Opaque, true);
      DoubleBuffered = false;
    }

    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        if (NoActivate) cp.ExStyle |= Native.WS_EX_NOACTIVATE;
        return cp;
      }
    }

    /// <summary>Show without stealing focus from whatever is being recorded.</summary>
    public void ShowInactive() {
      if (!IsHandleCreated) CreateHandle();
      Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
      _shown = true;
      Native.KeepOnTop(Handle);
      ApplyCaptureExclusion();
      Redraw();
    }

    public new void Hide() {
      HideTooltip();
      _shown = false;
      Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    public bool IsShown { get { return _shown && IsHandleCreated && Native.IsWindowVisible(Handle); } }

    /// <summary>
    /// Keep our own UI out of the user's recording. Reapplied on every show,
    /// because setting it before the window is visible silently does nothing.
    /// </summary>
    protected void ApplyCaptureExclusion() {
      if (IsHandleCreated) Native.ExcludeFromCapture(Handle);
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      Dpi.FromWindow(Handle);
      ApplyCaptureExclusion();
    }

    // ----------------------------------------------------------- rendering

    /// <summary>Subclasses lay out their hit regions and draw here.</summary>
    protected virtual void OnRender(Graphics g, Size size) { }

    /// <summary>Recomputed before every render so a resize or DPI change
    /// rebuilds the layout.</summary>
    protected virtual void Arrange(Size size) { }

    bool _rendering;
    Bitmap _surface;
    Graphics _surfaceGraphics;

    /// <summary>
    /// The drawing surface is kept between renders. While recording, the
    /// overlay redraws several times a second to move the timer and pulse the
    /// dot; allocating a fresh bitmap each time would churn a megabyte a second
    /// through the heap for a window the size of a postage stamp.
    /// </summary>
    void EnsureSurface() {
      if (_surface != null && _surface.Width == Width && _surface.Height == Height) return;
      ReleaseSurface();
      _surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
      _surfaceGraphics = Graphics.FromImage(_surface);
      Theme.Prepare(_surfaceGraphics);
    }

    void ReleaseSurface() {
      if (_surfaceGraphics != null) { _surfaceGraphics.Dispose(); _surfaceGraphics = null; }
      if (_surface != null) { _surface.Dispose(); _surface = null; }
    }

    public void Redraw() {
      if (!IsHandleCreated || _rendering) return;
      if (Width <= 0 || Height <= 0) return;

      _rendering = true;
      try {
        Hits.Clear();
        Arrange(Size);

        EnsureSurface();
        _surfaceGraphics.Clear(Color.Transparent);
        OnRender(_surfaceGraphics, Size);
        Push(_surface);
      } catch (Exception err) {
        Log.Warn("render failed: " + err.Message);
      } finally {
        _rendering = false;
      }
    }

    void Push(Bitmap bitmap) {
      IntPtr screenDc = Native.GetDC(IntPtr.Zero);
      IntPtr memDc = Native.CreateCompatibleDC(screenDc);
      IntPtr hBitmap = IntPtr.Zero;
      IntPtr previous = IntPtr.Zero;

      try {
        // A zero background keeps the premultiplied alpha intact rather than
        // compositing the bitmap onto an opaque colour.
        hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        previous = Native.SelectObject(memDc, hBitmap);

        var size = new Native.SIZE(); size.cx = Width; size.cy = Height;
        var src = new Native.POINT(); src.X = 0; src.Y = 0;
        var dst = new Native.POINT(); dst.X = Left; dst.Y = Top;

        var blend = new Native.BLENDFUNCTION();
        blend.BlendOp = Native.AC_SRC_OVER;
        blend.BlendFlags = 0;
        blend.SourceConstantAlpha = 255;
        blend.AlphaFormat = Native.AC_SRC_ALPHA;

        Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src,
                                   0, ref blend, Native.ULW_ALPHA);
      } finally {
        if (previous != IntPtr.Zero) Native.SelectObject(memDc, previous);
        if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
      }
    }

    // -------------------------------------------------------- hit testing

    protected Hit AddHit(string id, Rectangle bounds, bool enabled, string tooltip) {
      var h = new Hit();
      h.Id = id; h.Bounds = bounds; h.Enabled = enabled; h.Tooltip = tooltip;
      Hits.Add(h);
      return h;
    }

    protected Hit HitAt(Point p) {
      // Reverse order so the most recently added region wins where they
      // overlap, which matches the drawing order.
      for (int i = Hits.Count - 1; i >= 0; i--) {
        if (Hits[i].Bounds.Contains(p)) return Hits[i];
      }
      return null;
    }

    /// <summary>Called when an enabled region is clicked.</summary>
    protected virtual void OnHit(Hit hit) { }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      Hit h = HitAt(e.Location);
      string id = h != null && h.Enabled ? h.Id : null;
      if (id != Hovered) {
        Hovered = id;
        Redraw();
        ScheduleTooltip(h);
      }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      HideTooltip();
      if (e.Button != MouseButtons.Left) return;

      Hit h = HitAt(e.Location);
      if (h != null && h.Enabled) {
        Pressed = h.Id;
        Redraw();
        return;
      }

      // Not on a control: if it landed on the drag region, hand the window to
      // the system move loop, which is smoother than tracking it ourselves.
      if (DragRegion.Contains(e.Location)) {
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        OnDragFinished();
      }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left) return;

      string wasPressed = Pressed;
      Pressed = null;
      if (wasPressed == null) { Redraw(); return; }

      Hit h = HitAt(e.Location);
      Redraw();
      if (h != null && h.Enabled && h.Id == wasPressed) OnHit(h);
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      HideTooltip();
      if (Hovered != null || Pressed != null) {
        Hovered = null;
        Pressed = null;
        Redraw();
      }
    }

    protected virtual void OnDragFinished() { }

    // ----------------------------------------------------------- tooltips

    void ScheduleTooltip(Hit h) {
      HideTooltip();
      if (h == null || string.IsNullOrEmpty(h.Tooltip) || !h.Enabled) return;

      _tipFor = h.Id;
      if (_tipTimer == null) {
        _tipTimer = new Timer();
        _tipTimer.Interval = 550;
        _tipTimer.Tick += delegate {
          _tipTimer.Stop();
          ShowTooltip();
        };
      }
      _tipTimer.Stop();
      _tipTimer.Start();
    }

    void ShowTooltip() {
      if (_tipFor == null || !IsShown) return;
      Hit h = null;
      foreach (Hit candidate in Hits) if (candidate.Id == _tipFor) { h = candidate; break; }
      if (h == null || string.IsNullOrEmpty(h.Tooltip)) return;

      if (_tip == null) {
        _tip = new ToolTip();
        _tip.OwnerDraw = false;
        _tip.ShowAlways = true;
      }
      try {
        _tip.Show(h.Tooltip, this, h.Bounds.Left, h.Bounds.Bottom + Dpi.S(4), 4000);
      } catch (Exception) { }
    }

    protected void HideTooltip() {
      _tipFor = null;
      if (_tipTimer != null) _tipTimer.Stop();
      if (_tip != null) { try { _tip.Hide(this); } catch (Exception) { } }
    }

    // ---------------------------------------------------------------- DPI

    protected override void WndProc(ref Message m) {
      if (m.Msg == (int)Native.WM_DPICHANGED) {
        Dpi.Value = (int)(m.WParam.ToInt64() & 0xFFFF);
        OnDpiChanged();
        m.Result = IntPtr.Zero;
        return;
      }
      base.WndProc(ref m);
    }

    protected virtual void OnDpiChanged() { Redraw(); }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        if (_tipTimer != null) { _tipTimer.Dispose(); _tipTimer = null; }
        if (_tip != null) { _tip.Dispose(); _tip = null; }
        ReleaseSurface();
      }
      base.Dispose(disposing);
    }
  }
}
