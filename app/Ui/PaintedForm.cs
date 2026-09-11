using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// Base for the two opaque dialogs, the picker and the settings window.
  ///
  /// These are ordinary double-buffered windows rather than layered ones: they
  /// are rectangular, they host the few real text boxes that need a caret, and
  /// they need to take focus. Everything visible is still drawn by hand from
  /// the same tokens, so they match the overlay exactly.
  /// </summary>
  internal class PaintedForm : Form {

    protected readonly Dpi Dpi = new Dpi();
    protected readonly List<Hit> Hits = new List<Hit>();

    protected string Hovered;
    protected string PressedId;

    protected int TitleBarHeight = 44;
    protected string TitleText = "";

    /// <summary>ScrollY offset of the body region, when the subclass uses one.</summary>
    protected int ScrollY;
    protected int ContentHeight;
    protected Rectangle BodyRect;

    ToolTip _tip;

    public PaintedForm() {
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      BackColor = Theme.Bg;
      KeyPreview = true;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
               ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      Dpi.FromWindow(Handle);
    }

    protected override void OnShown(EventArgs e) {
      base.OnShown(e);
      // Keep dialogs out of the capture too. Must be after the window is
      // visible - applied earlier it silently does nothing.
      Native.ExcludeFromCapture(Handle);
      Native.KeepOnTop(Handle);
    }

    /// <summary>Centre on whichever monitor the cursor is on, which is where
    /// the user is looking.</summary>
    protected void CentreOnCursor(int width, int height) {
      Native.POINT p;
      Native.GetCursorPos(out p);
      var cursor = new Point(p.X, p.Y);
      Dpi.FromPoint(cursor);

      Rectangle wa = Screen.FromPoint(cursor).WorkingArea;
      int w = Math.Min(Dpi.S(width), wa.Width - Dpi.S(40));
      int h = Math.Min(Dpi.S(height), wa.Height - Dpi.S(40));
      SetBounds(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
    }

    // -------------------------------------------------------------- layout

    protected virtual void Relayout() { }

    protected override void OnResize(EventArgs e) {
      base.OnResize(e);
      Relayout();
      Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      Theme.Prepare(g);
      using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

      Hits.Clear();
      OnRender(g);
    }

    protected virtual void OnRender(Graphics g) { }

    // ---------------------------------------------------------- title bar

    protected void DrawTitleBar(Graphics g) {
      int h = Dpi.S(TitleBarHeight);
      Theme.Text(g, TitleText, Theme.Get(Dpi.S(13f), FontStyle.Bold), Theme.Fg,
                 new RectangleF(Dpi.S(16), 0, Width - Dpi.S(120), h));
      using (var pen = new Pen(Theme.Border, 1f)) g.DrawLine(pen, 0, h, Width, h);
    }

    protected void DrawIconButton(Graphics g, Hit h, string icon, Color? colourOverride) {
      Color colour = colourOverride ?? Theme.FgDim;
      if (Hovered == h.Id && h.Enabled) {
        Theme.FillRounded(g, h.Bounds, Dpi.S((float)Theme.RadiusSm), Theme.BgHover);
        if (colourOverride == null) colour = Theme.Fg;
      }
      if (!h.Enabled) colour = Theme.WithAlpha(colour, 77);

      float size = Dpi.S(17f);
      Icons.Draw(g, icon, new RectangleF(h.Bounds.X + (h.Bounds.Width - size) / 2f,
                                         h.Bounds.Y + (h.Bounds.Height - size) / 2f, size, size),
                 colour, false);
    }

    // ---------------------------------------------------------- hit testing

    protected Hit AddHit(string id, Rectangle bounds, bool enabled, string tooltip) {
      var h = new Hit();
      h.Id = id; h.Bounds = bounds; h.Enabled = enabled; h.Tooltip = tooltip;
      Hits.Add(h);
      return h;
    }

    protected Hit HitAt(Point p) {
      for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].Bounds.Contains(p)) return Hits[i];
      return null;
    }

    protected virtual void OnHit(Hit hit) { }
    protected virtual void OnHitDrag(Hit hit, Point p) { }

    Hit _dragging;

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);

      if (_dragging != null) { OnHitDrag(_dragging, e.Location); return; }

      Hit h = HitAt(e.Location);
      string id = h != null && h.Enabled ? h.Id : null;
      if (id != Hovered) {
        Hovered = id;
        Invalidate();
        ShowTip(h, e.Location);
      }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      HideTip();
      if (e.Button != MouseButtons.Left) return;

      Hit h = HitAt(e.Location);
      if (h != null && h.Enabled) {
        PressedId = h.Id;
        // Sliders want to follow the pointer, so they opt in by id prefix.
        if (h.Id.StartsWith("slider:", StringComparison.Ordinal)) {
          _dragging = h;
          OnHitDrag(h, e.Location);
        }
        Invalidate();
        return;
      }

      if (e.Location.Y < Dpi.S(TitleBarHeight)) {
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
      }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left) return;

      _dragging = null;
      string was = PressedId;
      PressedId = null;
      if (was == null) { Invalidate(); return; }

      Hit h = HitAt(e.Location);
      Invalidate();
      if (h != null && h.Enabled && h.Id == was && !was.StartsWith("slider:", StringComparison.Ordinal))
        OnHit(h);
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      HideTip();
      if (Hovered != null) { Hovered = null; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      if (BodyRect.Height <= 0) return;
      int max = Math.Max(0, ContentHeight - BodyRect.Height);
      int next = Math.Min(Math.Max(ScrollY - Math.Sign(e.Delta) * Dpi.S(60), 0), max);
      if (next == ScrollY) return;
      ScrollY = next;
      OnScrolled();
      Invalidate();
    }

    protected virtual void OnScrolled() { }

    // ------------------------------------------------------------ scrollbar

    protected void DrawScrollbar(Graphics g) {
      if (ContentHeight <= BodyRect.Height || BodyRect.Height <= 0) return;

      int w = Dpi.S(10);
      var track = new Rectangle(BodyRect.Right - w, BodyRect.Y, w, BodyRect.Height);
      float ratio = (float)BodyRect.Height / ContentHeight;
      int thumbH = Math.Max(Dpi.S(28), (int)(track.Height * ratio));
      int max = Math.Max(1, ContentHeight - BodyRect.Height);
      int thumbY = track.Y + (int)((track.Height - thumbH) * ((float)ScrollY / max));

      var thumb = new Rectangle(track.X + Dpi.S(2), thumbY, w - Dpi.S(4), thumbH);
      Theme.FillRounded(g, thumb, thumb.Width / 2f, Color.FromArgb(0x3a, 0x40, 0x48));
    }

    // ------------------------------------------------------------- tooltips

    void ShowTip(Hit h, Point at) {
      HideTip();
      if (h == null || string.IsNullOrEmpty(h.Tooltip)) return;
      if (_tip == null) { _tip = new ToolTip(); _tip.ShowAlways = true; }
      try { _tip.Show(h.Tooltip, this, at.X + Dpi.S(12), at.Y + Dpi.S(20), 4000); }
      catch (Exception) { }
    }

    void HideTip() {
      if (_tip != null) { try { _tip.Hide(this); } catch (Exception) { } }
    }

    // ------------------------------------------------------------------ DPI

    protected override void WndProc(ref Message m) {
      if (m.Msg == (int)Native.WM_DPICHANGED) {
        Dpi.Value = (int)(m.WParam.ToInt64() & 0xFFFF);
        var suggested = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(
          m.LParam, typeof(Native.RECT));
        SetBounds(suggested.Left, suggested.Top, suggested.Width, suggested.Height);
        Relayout();
        Invalidate();
        m.Result = IntPtr.Zero;
        return;
      }
      base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing) {
      if (disposing && _tip != null) { _tip.Dispose(); _tip = null; }
      base.Dispose(disposing);
    }
  }
}
