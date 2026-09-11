using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// Per-source audio control.
  ///
  /// Two different mechanisms sit side by side here, because Windows and the
  /// browser expose different things:
  ///   Applications - Core Audio session mute. Silences the app in the
  ///     recording and on your speakers, and is restored when you stop.
  ///   Browser tabs - the extension's tab mute, same trade-off.
  /// Recording a single tab needs neither: only that tab is captured.
  /// </summary>
  internal class MixerWindow : LayeredWindow {

    const int PanelW = 340, PanelH = 420;
    const int ShadowPad = 24;
    const int TitleH = 38;
    const int RowH = 34;
    const int HeadingH = 24;
    const int PadX = 13;
    const int SwitchW = 34, SwitchH = 19;

    class Row {
      public string Kind;          // capture | app | tab | message
      public string Id;
      public string Label;
      public string Meta;
      public bool On;
      public bool Live;
      public bool HasLiveDot;
      public string IconName;
      public uint Pid;
      public int TabId;
    }

    readonly App _app;
    readonly System.Windows.Forms.Timer _poll;
    readonly List<Row> _rows = new List<Row>();

    int _scroll;
    int _contentHeight;
    string _footer = "";
    volatile bool _refreshing;

    public MixerWindow(App app) {
      _app = app;
      NoActivate = false;      // it takes clicks, so it may take focus
      Text = "Audio mixer";

      // Sessions and tabs come and go while the panel is open; a slow poll
      // keeps it honest without being a drain. The window is destroyed on close.
      _poll = new System.Windows.Forms.Timer();
      _poll.Interval = 3000;
      _poll.Tick += delegate { RefreshSessions(); };
    }

    public void Open(Rectangle anchorPill) {
      int pad = Dpi.S(ShadowPad);
      SetBounds(0, 0, Dpi.S(PanelW) + pad * 2, Dpi.S(PanelH) + pad * 2);
      Reposition(anchorPill);
      ShowInactive();
      RefreshSessions();
      _poll.Start();
    }

    public void Reposition(Rectangle anchorPill) {
      int pad = Dpi.S(ShadowPad);
      int w = Dpi.S(PanelW), h = Dpi.S(PanelH);

      Rectangle wa = Screen.FromPoint(new Point(anchorPill.X, anchorPill.Y)).WorkingArea;
      int x = Math.Min(Math.Max(anchorPill.Right - w, wa.X), wa.Right - w);
      int y = Math.Min(anchorPill.Bottom + Dpi.S(8), wa.Bottom - h);

      SetBounds(x - pad, y - pad, w + pad * 2, h + pad * 2);
    }

    // ------------------------------------------------------------- refresh

    /// <summary>
    /// Session enumeration touches COM and, the first time it sees a process,
    /// reads that executable's version resource. Both are quick but neither
    /// belongs on the UI thread while the panel is being dragged around.
    /// </summary>
    public void RefreshSessions() {
      if (_refreshing) return;
      _refreshing = true;

      ThreadPool.QueueUserWorkItem(delegate {
        string error = null;
        List<AppSession> apps = null;
        try {
          apps = _app.Mixer.List(out error);
        } catch (Exception err) {
          error = err.Message;
        }

        try {
          if (IsHandleCreated && !IsDisposed) {
            List<AppSession> captured = apps;
            string capturedError = error;
            BeginInvoke((MethodInvoker)delegate {
              _refreshing = false;
              if (IsDisposed) return;
              Rebuild(captured, capturedError);
              Redraw();
            });
            return;
          }
        } catch (Exception) { }
        _refreshing = false;
      });
    }

    void Rebuild(List<AppSession> apps, string error) {
      _rows.Clear();
      Settings s = Settings.Current;

      _rows.Add(Heading("Capture", null));
      _rows.Add(CaptureRow("system", "System audio",
                        s.SystemAudioEnabled ? "Everything you hear" : "Not captured",
                        s.SystemAudioEnabled && !s.SystemAudioMuted, Icons.Speaker));
      _rows.Add(CaptureRow("mic", "Microphone",
                        s.MicEnabled ? "Your voice" : "Not captured",
                        s.MicEnabled && !s.MicMuted, Icons.Mic));

      _rows.Add(Heading("Applications", error != null ? "· unavailable" : null));
      if (error != null) {
        _rows.Add(Message("Could not read audio sessions: " + error));
      } else if (apps == null || apps.Count == 0) {
        _rows.Add(Message("No application is playing audio right now."));
      } else {
        foreach (AppSession a in apps) {
          var r = new Row();
          r.Kind = "app";
          r.Id = "app:" + a.Pid;
          r.Label = a.Name;
          r.Meta = "pid " + a.Pid + (a.Active ? " · playing" : " · idle");
          r.On = !a.Muted;
          r.Live = a.Active;
          r.HasLiveDot = true;
          r.IconName = Icons.AppBox;
          r.Pid = a.Pid;
          _rows.Add(r);
        }
      }

      _rows.Add(Heading("Browser tabs", null));
      if (!_app.Bridge.IsConnected) {
        _rows.Add(Message("Browser extension not connected. Load it from the extension folder to mute tabs individually."));
      } else {
        List<TabInfo> tabs = _app.Bridge.Tabs;
        if (tabs.Count == 0) {
          _rows.Add(Message("No tabs reported."));
        } else {
          // Tabs making noise are the ones worth acting on, so float them up.
          var sorted = new List<TabInfo>(tabs);
          sorted.Sort(delegate (TabInfo a, TabInfo b) {
            if (a.Audible != b.Audible) return a.Audible ? -1 : 1;
            return 0;
          });
          foreach (TabInfo t in sorted) {
            var r = new Row();
            r.Kind = "tab";
            r.Id = "tab:" + t.Id;
            r.Label = string.IsNullOrEmpty(t.Title) ? t.Url : t.Title;
            r.Meta = t.Audible ? "playing audio" : "silent";
            r.On = !t.Muted;
            r.Live = t.Audible;
            r.HasLiveDot = true;
            r.IconName = Icons.AppBox;
            r.TabId = t.Id;
            _rows.Add(r);
          }
        }
        _app.Bridge.RefreshTabs();
      }

      _footer = _app.CurrentSource != null && _app.CurrentSource.Kind == "tab"
        ? "Recording a single tab: only that tab's audio is captured, so other tabs are already excluded."
        : "Muting here silences the app or tab in the recording and on your speakers. Everything is restored when you stop.";
    }

    static Row Heading(string text, string note) {
      var r = new Row(); r.Kind = "heading"; r.Label = text; r.Meta = note; return r;
    }

    static Row Message(string text) {
      var r = new Row(); r.Kind = "message"; r.Label = text; return r;
    }

    static Row CaptureRow(string id, string label, string meta, bool on, string icon) {
      var r = new Row();
      r.Kind = "capture"; r.Id = "cap:" + id; r.Label = label; r.Meta = meta;
      r.On = on; r.IconName = icon;
      return r;
    }

    /// <summary>Reflect a settings change without a full session re-scan.</summary>
    public void OnStateChanged() {
      Settings s = Settings.Current;
      foreach (Row r in _rows) {
        if (r.Id == "cap:system") {
          r.On = s.SystemAudioEnabled && !s.SystemAudioMuted;
          r.Meta = s.SystemAudioEnabled ? "Everything you hear" : "Not captured";
        } else if (r.Id == "cap:mic") {
          r.On = s.MicEnabled && !s.MicMuted;
          r.Meta = s.MicEnabled ? "Your voice" : "Not captured";
        }
      }
      Redraw();
    }

    // -------------------------------------------------------------- layout

    Rectangle _panel, _scrollArea, _footerRect;

    protected override void Arrange(Size size) {
      int pad = Dpi.S(ShadowPad);
      _panel = new Rectangle(pad, pad, size.Width - pad * 2, size.Height - pad * 2);

      int titleH = Dpi.S(TitleH);
      int btn = Dpi.S(28);
      int y = _panel.Y + (titleH - btn) / 2;

      AddHit("close", new Rectangle(_panel.Right - Dpi.S(8) - btn, y, btn, btn), true, "Close");
      AddHit("refresh", new Rectangle(_panel.Right - Dpi.S(8) - btn * 2 - Dpi.S(2), y, btn, btn), true, "Refresh");

      DragRegion = new Rectangle(_panel.X, _panel.Y, _panel.Width - Dpi.S(80), titleH);

      // Footer height depends on how the sentence wraps.
      float footerTextH;
      using (var probe = new Bitmap(1, 1))
      using (Graphics g = Graphics.FromImage(probe)) {
        footerTextH = Theme.MeasureWrapped(g, _footer, Theme.Get(Dpi.S(10.5f), FontStyle.Regular),
                                           _panel.Width - Dpi.S(PadX) * 2);
      }
      int footerH = (int)footerTextH + Dpi.S(16);
      _footerRect = new Rectangle(_panel.X, _panel.Bottom - footerH, _panel.Width, footerH);

      _scrollArea = new Rectangle(_panel.X, _panel.Y + titleH, _panel.Width,
                                  _panel.Height - titleH - footerH);

      // Rows, offset by the scroll position and clipped to the scroll area.
      int rowY = _scrollArea.Y + Dpi.S(6) - _scroll;
      foreach (Row r in _rows) {
        int h = RowHeight(r);
        if (r.Kind != "heading" && r.Kind != "message") {
          var sw = new Rectangle(_panel.Right - Dpi.S(PadX) - Dpi.S(SwitchW),
                                 rowY + (h - Dpi.S(SwitchH)) / 2, Dpi.S(SwitchW), Dpi.S(SwitchH));
          if (sw.Bottom > _scrollArea.Y && sw.Top < _scrollArea.Bottom)
            AddHit(r.Id, sw, true, null);
        }
        rowY += h;
      }
      _contentHeight = rowY + _scroll - (_scrollArea.Y + Dpi.S(6)) + Dpi.S(6);
    }

    int RowHeight(Row r) {
      if (r.Kind == "heading") return Dpi.S(HeadingH);
      if (r.Kind == "message") return MessageHeight(r.Label);
      return Dpi.S(RowH);
    }

    int MessageHeight(string text) {
      using (var probe = new Bitmap(1, 1))
      using (Graphics g = Graphics.FromImage(probe)) {
        float h = Theme.MeasureWrapped(g, text, Theme.Get(Dpi.S(11.5f), FontStyle.Regular),
                                       _panel.Width - Dpi.S(PadX) * 2);
        return (int)h + Dpi.S(16);
      }
    }

    // ----------------------------------------------------------- rendering

    protected override void OnRender(Graphics g, Size size) {
      Bitmap shadow = Theme.Shadow(_panel.Width, _panel.Height, Dpi.S((float)Theme.Radius), Dpi.S(34), 153);
      if (shadow != null) {
        int blur = Dpi.S(34);
        g.DrawImageUnscaled(shadow, _panel.X - blur * 2, _panel.Y - blur * 2 + Dpi.S(10));
      }

      Color glass = Color.FromArgb(247, Theme.Bg.R, Theme.Bg.G, Theme.Bg.B);
      Theme.FillAndStroke(g, _panel, Dpi.S((float)Theme.Radius), glass, Theme.BorderStrong, 1f);

      // Title bar
      int titleH = Dpi.S(TitleH);
      Theme.Text(g, "Audio", Theme.Get(Dpi.S(12.5f), FontStyle.Bold), Theme.Fg,
                 new RectangleF(_panel.X + Dpi.S(13), _panel.Y, _panel.Width - Dpi.S(90), titleH));
      using (var pen = new Pen(Theme.Border, 1f))
        g.DrawLine(pen, _panel.X, _panel.Y + titleH, _panel.Right, _panel.Y + titleH);

      foreach (Hit h in Hits) {
        if (h.Id == "close") DrawIconButton(g, h, Icons.Close);
        else if (h.Id == "refresh") DrawIconButton(g, h, Icons.Refresh);
      }

      // Scrolling body
      var state = g.Save();
      g.SetClip(_scrollArea);
      int y = _scrollArea.Y + Dpi.S(6) - _scroll;
      foreach (Row r in _rows) {
        int h = RowHeight(r);
        if (y + h > _scrollArea.Y && y < _scrollArea.Bottom) DrawRow(g, r, y, h);
        y += h;
      }
      g.Restore(state);

      // Footer
      using (var pen = new Pen(Theme.Border, 1f))
        g.DrawLine(pen, _footerRect.X, _footerRect.Y, _footerRect.Right, _footerRect.Y);
      Theme.WrappedText(g, _footer, Theme.Get(Dpi.S(10.5f), FontStyle.Regular), Theme.FgFaint,
                        new RectangleF(_footerRect.X + Dpi.S(PadX), _footerRect.Y + Dpi.S(8),
                                       _footerRect.Width - Dpi.S(PadX) * 2, _footerRect.Height));
    }

    void DrawRow(Graphics g, Row r, int y, int h) {
      if (r.Kind == "heading") {
        string text = r.Label.ToUpperInvariant();
        Font f = Theme.Get(Dpi.S(10.5f), FontStyle.Bold);
        var box = new RectangleF(_panel.X + Dpi.S(PadX), y + Dpi.S(4), _panel.Width - Dpi.S(PadX) * 2, h - Dpi.S(4));
        Theme.Text(g, text, f, Theme.FgFaint, box);
        if (!string.IsNullOrEmpty(r.Meta)) {
          float w = Theme.MeasureWidth(g, text, f);
          Theme.Text(g, r.Meta, Theme.Get(Dpi.S(10.5f), FontStyle.Regular), Theme.Warn,
                     new RectangleF(box.X + w + Dpi.S(6), box.Y, box.Width - w - Dpi.S(6), box.Height));
        }
        return;
      }

      if (r.Kind == "message") {
        Theme.WrappedText(g, r.Label, Theme.Get(Dpi.S(11.5f), FontStyle.Regular), Theme.FgFaint,
                          new RectangleF(_panel.X + Dpi.S(PadX), y + Dpi.S(8),
                                         _panel.Width - Dpi.S(PadX) * 2, h));
        return;
      }

      bool hovered = false;
      foreach (Hit hit in Hits) {
        if (hit.Id == r.Id && Hovered == r.Id) { hovered = true; break; }
      }
      if (hovered) {
        using (var b = new SolidBrush(Color.FromArgb(10, 255, 255, 255)))
          g.FillRectangle(b, _panel.X, y, _panel.Width, h);
      }

      int x = _panel.X + Dpi.S(PadX);

      if (r.HasLiveDot) {
        int d = Dpi.S(6);
        Color c = r.Live ? Theme.Ok : Theme.WithAlpha(Theme.FgFaint, 128);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + (h - d) / 2, d, d);
        x += d + Dpi.S(9);
      }

      int icon = Dpi.S(16);
      var iconBox = new RectangleF(x, y + (h - icon) / 2f, icon, icon);

      Icons.Draw(g, r.IconName ?? Icons.AppBox, iconBox,
                      r.On ? Theme.FgDim : Theme.FgFaint, false);
      x += icon + Dpi.S(9);

      int right = _panel.Right - Dpi.S(PadX) - Dpi.S(SwitchW) - Dpi.S(9);
      int textWidth = Math.Max(Dpi.S(20), right - x);

      Color labelColour = r.On ? Theme.Fg : Theme.FgFaint;
      float lineH = Dpi.S(15f);
      float top = y + (h - lineH * 2) / 2f;

      Theme.Text(g, r.Label, Theme.Get(Dpi.S(12f), FontStyle.Regular), labelColour,
                 new RectangleF(x, top, textWidth, lineH));
      if (!string.IsNullOrEmpty(r.Meta))
        Theme.Text(g, r.Meta, Theme.Get(Dpi.S(10f), FontStyle.Regular), Theme.FgFaint,
                   new RectangleF(x, top + lineH, textWidth, lineH));

      foreach (Hit hit in Hits) {
        if (hit.Id != r.Id) continue;
        Widgets.DrawSwitch(g, hit.Bounds, r.On, Dpi);
        break;
      }
    }

    void DrawIconButton(Graphics g, Hit h, string icon) {
      Color colour = Theme.FgDim;
      if (Hovered == h.Id) {
        Theme.FillRounded(g, h.Bounds, Dpi.S((float)Theme.RadiusSm), Theme.BgHover);
        colour = Theme.Fg;
      }
      float size = Dpi.S(16f);
      Icons.Draw(g, icon, new RectangleF(h.Bounds.X + (h.Bounds.Width - size) / 2f,
                                         h.Bounds.Y + (h.Bounds.Height - size) / 2f, size, size),
                 colour, false);
    }

    // -------------------------------------------------------------- input

    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      int max = Math.Max(0, _contentHeight - _scrollArea.Height);
      int next = _scroll - Math.Sign(e.Delta) * Dpi.S(48);
      next = Math.Min(Math.Max(next, 0), max);
      if (next == _scroll) return;
      _scroll = next;
      Redraw();
    }

    protected override void OnHit(Hit hit) {
      if (hit.Id == "close") { _app.CloseMixer(); return; }
      if (hit.Id == "refresh") { RefreshSessions(); return; }

      foreach (Row r in _rows) {
        if (r.Id != hit.Id) continue;

        if (r.Kind == "capture") {
          if (r.Id == "cap:system") _app.ToggleSystemAudio();
          else _app.ToggleMic();
          return;
        }

        bool wantMuted = r.On;                 // switch was on, so turn sound off
        if (r.Kind == "app") {
          if (!_app.Mixer.SetMuted(r.Pid, wantMuted)) { RefreshSessions(); return; }
          r.On = !wantMuted;
          Redraw();
        } else if (r.Kind == "tab") {
          if (!_app.Bridge.SetTabMuted(r.TabId, wantMuted)) { RefreshSessions(); return; }
          r.On = !wantMuted;
          Redraw();
        }
        return;
      }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Escape) _app.CloseMixer();
    }

    protected override void Dispose(bool disposing) {
      if (disposing && _poll != null) { _poll.Stop(); _poll.Dispose(); }
      base.Dispose(disposing);
    }
  }
}
