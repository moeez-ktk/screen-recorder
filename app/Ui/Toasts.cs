using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  internal enum ToastKind { Info, Success, Warn, Error }

  /// <summary>
  /// The toast stack, ported from the CSS notifications the renderers used to
  /// share. One window holds the whole stack rather than one window per
  /// message, so a burst of notifications costs a single redraw.
  /// </summary>
  internal class ToastHost : LayeredWindow {

    class Entry {
      public string Message;
      public ToastKind Kind;
      public DateTime Expires;
      public float Height;
    }

    const int PanelWidth = 300;
    const int PadX = 11, PadY = 8, Gap = 6, Radius = Theme.RadiusSm;
    const int ShadowPad = 20;
    const int MaxVisible = 4;

    readonly List<Entry> _entries = new List<Entry>();
    readonly Timer _timer;
    Func<Rectangle> _anchorProvider;

    public ToastHost() {
      NoActivate = true;
      Text = "Light Recorder notifications";

      _timer = new Timer();
      _timer.Interval = 250;
      _timer.Tick += delegate { Sweep(); };
    }

    /// <summary>Where the stack hangs from - normally just below the overlay
    /// pill, so notifications appear where they used to.</summary>
    public void AnchorTo(Func<Rectangle> provider) { _anchorProvider = provider; }

    public void Show(string message, ToastKind kind) {
      if (string.IsNullOrEmpty(message)) return;

      // Errors linger; routine confirmations get out of the way quickly.
      int life = kind == ToastKind.Error ? 7000 : kind == ToastKind.Warn ? 5500 : 3200;

      var entry = new Entry();
      entry.Message = message;
      entry.Kind = kind;
      entry.Expires = DateTime.UtcNow.AddMilliseconds(life);

      _entries.Add(entry);
      while (_entries.Count > MaxVisible) _entries.RemoveAt(0);

      Reflow();
      if (!_timer.Enabled) _timer.Start();
    }

    void Sweep() {
      DateTime now = DateTime.UtcNow;
      int before = _entries.Count;
      _entries.RemoveAll(delegate (Entry e) { return e.Expires <= now; });
      if (_entries.Count == before) return;

      if (_entries.Count == 0) {
        _timer.Stop();
        Hide();
        return;
      }
      Reflow();
    }

    void Reflow() {
      if (_entries.Count == 0) { Hide(); return; }

      int pad = Dpi.S(ShadowPad);
      int width = Dpi.S(PanelWidth);

      // Measure first: a long error message wraps to two or three lines.
      float total = 0;
      using (var probe = new Bitmap(1, 1))
      using (Graphics g = Graphics.FromImage(probe)) {
        Font font = Theme.Get(Dpi.S(12f), FontStyle.Regular);
        foreach (Entry e in _entries) {
          float textWidth = width - Dpi.S(PadX) * 2 - Dpi.S(3);
          float h = Theme.MeasureWrapped(g, e.Message, font, textWidth);
          e.Height = Math.Max(Dpi.S(30), h + Dpi.S(PadY) * 2);
          total += e.Height + Dpi.S(Gap);
        }
      }
      if (total > 0) total -= Dpi.S(Gap);

      Rectangle anchor = _anchorProvider != null
        ? _anchorProvider()
        : new Rectangle(Screen.PrimaryScreen.WorkingArea.Right - width - Dpi.S(16),
                        Screen.PrimaryScreen.WorkingArea.Top + Dpi.S(70), width, 0);

      int x = anchor.Right - width;
      int y = anchor.Bottom + Dpi.S(8);

      Rectangle wa = Screen.FromPoint(new Point(anchor.Right, anchor.Bottom)).WorkingArea;
      x = Math.Min(Math.Max(x, wa.X), wa.Right - width);
      y = Math.Min(y, wa.Bottom - (int)total - Dpi.S(8));

      SetBounds(x - pad, y - pad, width + pad * 2, (int)total + pad * 2);
      if (!IsShown) ShowInactive(); else Redraw();
    }

    protected override void OnRender(Graphics g, Size size) {
      int pad = Dpi.S(ShadowPad);
      int width = size.Width - pad * 2;
      Font font = Theme.Get(Dpi.S(12f), FontStyle.Regular);

      float y = pad;
      foreach (Entry e in _entries) {
        var box = new RectangleF(pad, y, width, e.Height);

        Bitmap shadow = Theme.Shadow((int)box.Width, (int)box.Height, Dpi.S((float)Radius), Dpi.S(20), 115);
        if (shadow != null) {
          int blur = Dpi.S(20);
          g.DrawImageUnscaled(shadow, (int)box.X - blur * 2, (int)box.Y - blur * 2 + Dpi.S(6));
        }

        Theme.FillAndStroke(g, box, Dpi.S((float)Radius), Theme.BgRaise, Theme.BorderStrong, 1f);

        // The 3px accent stripe down the left edge, clipped to the rounded
        // corner so it does not poke out.
        Color accent = e.Kind == ToastKind.Success ? Theme.Ok
                     : e.Kind == ToastKind.Error ? Theme.Rec
                     : e.Kind == ToastKind.Warn ? Theme.Warn
                     : Theme.FgDim;
        using (var clip = Theme.RoundedRect(box, Dpi.S((float)Radius))) {
          var state = g.Save();
          g.SetClip(clip);
          using (var b = new SolidBrush(accent))
            g.FillRectangle(b, box.X, box.Y, Dpi.S(3f), box.Height);
          g.Restore(state);
        }

        var textBox = new RectangleF(box.X + Dpi.S(PadX) + Dpi.S(3), box.Y + Dpi.S(PadY),
                                     box.Width - Dpi.S(PadX) * 2 - Dpi.S(3), box.Height - Dpi.S(PadY) * 2);
        Theme.WrappedText(g, e.Message, font, Theme.Fg, textBox);

        y += e.Height + Dpi.S(Gap);
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing && _timer != null) { _timer.Stop(); _timer.Dispose(); }
      base.Dispose(disposing);
    }
  }
}
