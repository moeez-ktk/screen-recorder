using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// The controls from theme.css, drawn rather than themed.
  ///
  /// Windows' own combo boxes and track bars cannot be made to look like this
  /// without owner-drawing most of them anyway, and a drawn control has no HWND,
  /// no window class and no message pump of its own - which is the difference
  /// between a settings window that costs a few hundred kilobytes and one that
  /// costs several megabytes of window handles.
  /// </summary>
  internal static class Widgets {

    /// <summary>34x19 track, 14px knob, sliding 15px. Straight from the CSS.</summary>
    public static void DrawSwitch(Graphics g, Rectangle r, bool on, Dpi dpi) {
      Color track = on ? Theme.Accent : Theme.BgHover;
      Theme.FillRounded(g, r, r.Height / 2f, track);

      float inset = dpi.S(2.5f);
      float knob = r.Height - inset * 2;
      float x = on ? r.Right - inset - knob : r.X + inset;

      using (var b = new SolidBrush(on ? Color.White : Theme.FgDim))
        g.FillEllipse(b, x, r.Y + inset, knob, knob);
    }

    public static void DrawSlider(Graphics g, Rectangle r, double value01, Dpi dpi, bool hovered) {
      if (value01 < 0) value01 = 0; else if (value01 > 1) value01 = 1;

      int trackH = dpi.S(4);
      var track = new Rectangle(r.X, r.Y + (r.Height - trackH) / 2, r.Width, trackH);
      Theme.FillRounded(g, track, trackH / 2f, Theme.BgHover);

      int filled = (int)(r.Width * value01);
      if (filled > 0)
        Theme.FillRounded(g, new Rectangle(track.X, track.Y, filled, trackH), trackH / 2f, Theme.Accent);

      int knob = dpi.S(hovered ? 15 : 13);
      float kx = r.X + filled - knob / 2f;
      kx = Math.Max(r.X - knob / 4f, Math.Min(kx, r.Right - knob * 0.75f));
      using (var b = new SolidBrush(Theme.Accent))
        g.FillEllipse(b, kx, r.Y + (r.Height - knob) / 2f, knob, knob);
      using (var pen = new Pen(Theme.Bg, dpi.S(2f)))
        g.DrawEllipse(pen, kx, r.Y + (r.Height - knob) / 2f, knob, knob);
    }

    public enum ButtonStyle { Normal, Primary }

    public static void DrawButton(Graphics g, Rectangle r, string text, ButtonStyle style,
                                  bool hovered, bool pressed, bool enabled, Dpi dpi) {
      Color fill, border, fg;

      if (style == ButtonStyle.Primary) {
        fill = hovered ? Theme.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
        border = fill;
        fg = Color.White;
      } else {
        fill = hovered ? Theme.BgHover : Theme.BgRaise;
        border = hovered ? Theme.BorderStrong : Theme.Border;
        fg = Theme.Fg;
      }

      if (!enabled) {
        fill = Theme.WithAlpha(fill, 115);
        border = Theme.WithAlpha(border, 115);
        fg = Theme.WithAlpha(fg, 115);
      }

      Rectangle box = r;
      if (pressed && enabled) box.Offset(0, Math.Max(1, dpi.S(1)));

      Theme.FillAndStroke(g, box, dpi.S((float)Theme.RadiusSm), fill, border, 1f);
      Theme.TextCentred(g, text, Theme.Get(dpi.S(13f),
                        style == ButtonStyle.Primary ? FontStyle.Bold : FontStyle.Regular), fg, box);
    }

    /// <summary>A drawn <select>: rounded field with the value and a chevron.</summary>
    public static void DrawSelect(Graphics g, Rectangle r, string text, bool hovered, bool open, Dpi dpi) {
      Color border = (hovered || open) ? Theme.Accent : Theme.Border;
      Theme.FillAndStroke(g, r, dpi.S((float)Theme.RadiusSm), Theme.BgRaise, border, 1f);

      int chevron = dpi.S(14);
      var textBox = new RectangleF(r.X + dpi.S(9), r.Y, r.Width - dpi.S(9) - chevron - dpi.S(10), r.Height);
      Theme.Text(g, text, Theme.Get(dpi.S(13f), FontStyle.Regular), Theme.Fg, textBox);

      Icons.Draw(g, Icons.ChevronDown,
                 new RectangleF(r.Right - dpi.S(8) - chevron, r.Y + (r.Height - chevron) / 2f, chevron, chevron),
                 Theme.FgDim, false);
    }

    public static void DrawFieldBox(Graphics g, Rectangle r, bool focused, Dpi dpi) {
      Theme.FillAndStroke(g, r, dpi.S((float)Theme.RadiusSm), Theme.BgRaise,
                          focused ? Theme.Accent : Theme.Border, 1f);
    }

    /// <summary>Section heading: 10.5px, uppercase, letter-spaced, faint.</summary>
    public static void DrawHeading(Graphics g, string text, RectangleF box, Dpi dpi) {
      Theme.Text(g, text.ToUpperInvariant(), Theme.Get(dpi.S(10.5f), FontStyle.Bold), Theme.FgFaint, box);
    }

    /// <summary>A themed TextBox for the handful of places that genuinely need
    /// a caret, selection and clipboard. Everything else is drawn.</summary>
    public static TextBox MakeTextBox(bool readOnly, Dpi dpi) {
      var t = new TextBox();
      t.BorderStyle = BorderStyle.None;
      t.BackColor = Theme.BgRaise;
      t.ForeColor = Theme.Fg;
      t.ReadOnly = readOnly;
      t.Font = Theme.Get(dpi.S(13f), FontStyle.Regular);
      return t;
    }
  }

  // ==========================================================================

  /// <summary>
  /// The dropdown half of a drawn select: a small borderless list that closes
  /// on choose, on Escape, or on losing focus.
  /// </summary>
  internal class PopupList : Form {

    public class Item {
      public string Value;
      public string Text;
      public Item(string value, string text) { Value = value; Text = text; }
    }

    const int RowH = 28;
    const int MaxVisible = 12;

    readonly List<Item> _items;
    readonly Dpi _dpi;
    int _hover = -1;
    int _selected;
    int _scroll;

    public event Action<string> Chosen;

    public PopupList(List<Item> items, string selectedValue, Dpi dpi, int width) {
      _items = items;
      _dpi = dpi;

      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      TopMost = true;
      BackColor = Theme.BgRaise;
      DoubleBuffered = true;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
               ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

      _selected = 0;
      for (int i = 0; i < items.Count; i++) if (items[i].Value == selectedValue) { _selected = i; break; }

      int visible = Math.Min(items.Count, MaxVisible);
      Width = width;
      Height = visible * dpi.S(RowH) + dpi.S(8);

      // Keep the current choice on screen when the list is long.
      if (_selected >= visible) _scroll = (_selected - visible + 1) * dpi.S(RowH);
    }

    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
        cp.ClassStyle |= 0x00020000;                  // CS_DROPSHADOW
        return cp;
      }
    }

    protected override bool ShowWithoutActivation { get { return false; } }

    public void Open(Point screenLocation) {
      Rectangle wa = Screen.FromPoint(screenLocation).WorkingArea;
      int x = Math.Min(Math.Max(screenLocation.X, wa.X), wa.Right - Width);
      int y = screenLocation.Y;
      if (y + Height > wa.Bottom) y = Math.Max(wa.Y, y - Height - _dpi.S(34));
      Location = new Point(x, y);
      Show();
      Native.ExcludeFromCapture(Handle);
      Focus();
    }

    protected override void OnDeactivate(EventArgs e) {
      base.OnDeactivate(e);
      Close();
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      Theme.Prepare(g);

      var box = new Rectangle(0, 0, Width - 1, Height - 1);
      Theme.FillAndStroke(g, box, _dpi.S((float)Theme.RadiusSm), Theme.BgRaise, Theme.BorderStrong, 1f);

      int rowH = _dpi.S(RowH);
      int y = _dpi.S(4) - _scroll;
      Font font = Theme.Get(_dpi.S(13f), FontStyle.Regular);

      for (int i = 0; i < _items.Count; i++) {
        var r = new Rectangle(_dpi.S(4), y, Width - _dpi.S(8), rowH);
        if (r.Bottom > 0 && r.Top < Height) {
          if (i == _hover) Theme.FillRounded(g, r, _dpi.S((float)Theme.RadiusSm) - 1, Theme.BgHover);
          else if (i == _selected) Theme.FillRounded(g, r, _dpi.S((float)Theme.RadiusSm) - 1, Theme.AccentDim);

          Theme.Text(g, _items[i].Text, font, i == _selected ? Theme.Accent : Theme.Fg,
                     new RectangleF(r.X + _dpi.S(8), r.Y, r.Width - _dpi.S(16), r.Height));
        }
        y += rowH;
      }
    }

    int IndexAt(Point p) {
      int rowH = _dpi.S(RowH);
      int i = (p.Y - _dpi.S(4) + _scroll) / rowH;
      return (i >= 0 && i < _items.Count) ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      int i = IndexAt(e.Location);
      if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e) {
      base.OnMouseClick(e);
      int i = IndexAt(e.Location);
      if (i < 0) return;
      Action<string> h = Chosen;
      if (h != null) h(_items[i].Value);
      Close();
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      int max = Math.Max(0, _items.Count * _dpi.S(RowH) + _dpi.S(8) - Height);
      _scroll = Math.Min(Math.Max(_scroll - Math.Sign(e.Delta) * _dpi.S(RowH) * 2, 0), max);
      Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Escape) { Close(); return; }
      if (e.KeyCode == Keys.Down) { _selected = Math.Min(_selected + 1, _items.Count - 1); Invalidate(); }
      if (e.KeyCode == Keys.Up) { _selected = Math.Max(_selected - 1, 0); Invalidate(); }
      if (e.KeyCode == Keys.Enter) {
        Action<string> h = Chosen;
        if (h != null) h(_items[_selected].Value);
        Close();
      }
    }
  }
}
