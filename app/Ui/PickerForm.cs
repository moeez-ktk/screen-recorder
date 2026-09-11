using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// Source picker.
  ///
  /// Thumbnails are captured only while this window is open, and everything is
  /// released when it closes, so none of this costs anything during a recording
  /// or while the app sits in the tray.
  /// </summary>
  internal class PickerForm : PaintedForm {

    const int CardMinW = 220, CardGap = 12, PagePad = 16;
    const int TabsH = 40, FooterH = 52;

    readonly App _app;

    string _tab = "screens";
    bool _loading = true;

    List<Source> _screens = new List<Source>();
    List<Source> _windows = new List<Source>();
    Source _chosen;

    readonly Dictionary<string, Rectangle> _cardRects = new Dictionary<string, Rectangle>();

    public PickerForm(App app) {
      _app = app;
      TitleText = "Choose what to record";
      CentreOnCursor(1000, 660);

      // Open on whatever is already selected.
      _chosen = app.CurrentSource;
      if (_chosen != null) {
        if (_chosen.Kind == "window") _tab = "windows";
        else if (_chosen.Kind == "tab") _tab = "browser";
      }

      LoadSources();
    }

    // --------------------------------------------------------------- loading

    void LoadSources() {
      _loading = true;
      Invalidate();

      ThreadPool.QueueUserWorkItem(delegate {
        List<Source> screens = null, windows = null;
        try {
          screens = Sources.Screens(true);
          windows = Sources.Windows(true);
        } catch (Exception err) {
          Log.Warn("source enumeration failed: " + err.Message);
        }

        try {
          if (!IsHandleCreated || IsDisposed) { DisposeAll(screens); DisposeAll(windows); return; }
          List<Source> s = screens, w = windows;
          BeginInvoke((MethodInvoker)delegate {
            if (IsDisposed) { DisposeAll(s); DisposeAll(w); return; }
            DisposeAll(_screens);
            DisposeAll(_windows);
            _screens = s ?? new List<Source>();
            _windows = w ?? new List<Source>();
            _loading = false;
            Relayout();
            Invalidate();
          });
        } catch (Exception) {
          DisposeAll(screens);
          DisposeAll(windows);
        }
      });
    }

    static void DisposeAll(List<Source> list) {
      if (list == null) return;
      foreach (Source s in list) s.DisposeImages();
    }

    List<Source> Tabs() {
      var list = new List<Source>();
      foreach (TabInfo t in _app.Bridge.Tabs) {
        var s = new Source();
        s.Kind = "tab";
        s.Id = "tab:" + t.Id;
        s.TabId = t.Id;
        s.Name = string.IsNullOrEmpty(t.Title) ? t.Url : t.Title;
        s.Url = t.Url;
        s.Audible = t.Audible;
        list.Add(s);
      }
      return list;
    }

    List<Source> VisibleItems() {
      if (_tab == "screens") return _screens;
      if (_tab == "windows") return _windows;
      return Tabs();
    }

    // ---------------------------------------------------------------- layout

    Rectangle _tabsRect;

    protected override void Relayout() {
      int titleH = Dpi.S(TitleBarHeight);
      _tabsRect = new Rectangle(0, titleH, Width, Dpi.S(TabsH));
      BodyRect = new Rectangle(0, _tabsRect.Bottom, Width, Height - _tabsRect.Bottom - Dpi.S(FooterH));

      int pad = Dpi.S(PagePad);
      int gap = Dpi.S(CardGap);
      int available = Math.Max(Dpi.S(CardMinW), BodyRect.Width - pad * 2);
      int columns = Math.Max(1, (available + gap) / (Dpi.S(CardMinW) + gap));
      int cardW = (available - gap * (columns - 1)) / columns;
      int cardH = (int)(cardW * 9.0 / 16.0) + Dpi.S(40);

      _cardRects.Clear();
      List<Source> items = VisibleItems();
      for (int i = 0; i < items.Count; i++) {
        int col = i % columns, row = i / columns;
        _cardRects[items[i].Id] = new Rectangle(
          pad + col * (cardW + gap),
          pad + row * (cardH + gap),
          cardW, cardH);
      }

      int rows = (items.Count + columns - 1) / columns;
      ContentHeight = rows > 0 ? pad * 2 + rows * cardH + (rows - 1) * gap : 0;
      if (ScrollY > Math.Max(0, ContentHeight - BodyRect.Height))
        ScrollY = Math.Max(0, ContentHeight - BodyRect.Height);
    }

    // ------------------------------------------------------------- rendering

    protected override void OnRender(Graphics g) {
      if (BodyRect.Width == 0) Relayout();

      DrawTitleBar(g);
      Hit close = AddHit("close", AddTitleButtonRect(0), true, "Close");
      Rectangle refreshRect = TitleTextButton(g, "Refresh", 1);
      AddHit("refresh", refreshRect, true, null);

      DrawIconButton(g, close, Icons.Close, null);
      Widgets.DrawButton(g, refreshRect, "Refresh", Widgets.ButtonStyle.Normal,
                          Hovered == "refresh", PressedId == "refresh", true, Dpi);

      DrawTabs(g);
      DrawBody(g);
      DrawFooter(g);
    }

    Rectangle AddTitleButtonRect(int indexFromRight) {
      int h = Dpi.S(TitleBarHeight);
      int btn = Dpi.S(30);
      return new Rectangle(Width - Dpi.S(8) - btn, (h - btn) / 2, btn, btn);
    }

    Rectangle TitleTextButton(Graphics g, string text, int slot) {
      int h = Dpi.S(TitleBarHeight);
      int w = (int)Theme.MeasureWidth(g, text, Theme.Get(Dpi.S(13f), FontStyle.Regular)) + Dpi.S(28);
      int height = Dpi.S(30);
      return new Rectangle(Width - Dpi.S(8) - Dpi.S(30) - Dpi.S(8) - w, (h - height) / 2, w, height);
    }

    void DrawTabs(Graphics g) {
      string[] ids = { "screens", "windows", "browser" };
      string[] labels = { "Entire screen", "Application window", "Browser tab" };
      int[] counts = { _screens.Count, _windows.Count, _app.Bridge.Tabs.Count };

      Font font = Theme.Get(Dpi.S(12.5f), FontStyle.Regular);
      int x = Dpi.S(16);

      for (int i = 0; i < ids.Length; i++) {
        string label = labels[i];
        float w = Theme.MeasureWidth(g, label, font) + Dpi.S(28);
        if (counts[i] > 0) w += Dpi.S(24);

        var r = new Rectangle(x, _tabsRect.Y + Dpi.S(10), (int)w, _tabsRect.Height - Dpi.S(10));
        AddHit("tab:" + ids[i], r, true, null);

        bool active = _tab == ids[i];
        if (Hovered == "tab:" + ids[i] && !active)
          Theme.FillRounded(g, new Rectangle(r.X, r.Y, r.Width, r.Height - Dpi.S(2)),
                            Dpi.S((float)Theme.RadiusSm), Theme.BgRaise);

        var textBox = new RectangleF(r.X + Dpi.S(14), r.Y, r.Width - Dpi.S(28), r.Height);
        Theme.Text(g, label, font, active ? Theme.Fg : Theme.FgDim, textBox);

        if (counts[i] > 0) {
          float labelW = Theme.MeasureWidth(g, label, font);
          var badge = new RectangleF(r.X + Dpi.S(14) + labelW + Dpi.S(6),
                                     r.Y + (r.Height - Dpi.S(16)) / 2f, Dpi.S(22), Dpi.S(16));
          Theme.FillRounded(g, badge, badge.Height / 2f, Theme.BgHover);
          Theme.TextCentred(g, counts[i].ToString(), Theme.Get(Dpi.S(10.5f), FontStyle.Regular),
                            Theme.FgFaint, badge);
        }

        if (active) {
          using (var b = new SolidBrush(Theme.Accent))
            g.FillRectangle(b, r.X, r.Bottom - Dpi.S(2), r.Width, Dpi.S(2));
        }
        x += (int)w + Dpi.S(2);
      }

      using (var pen = new Pen(Theme.Border, 1f))
        g.DrawLine(pen, 0, _tabsRect.Bottom, Width, _tabsRect.Bottom);
    }

    void DrawBody(Graphics g) {
      var state = g.Save();
      g.SetClip(BodyRect);
      g.TranslateTransform(BodyRect.X, BodyRect.Y - ScrollY);

      if (_loading) {
        RestoreAndMessage(g, state, "Looking for sources…", null, null);
        return;
      }

      if (_tab == "browser" && !_app.Bridge.IsConnected) {
        RestoreAndMessage(g, state,
          "The browser extension is not connected.",
          "Recording a single tab - and muting tabs individually - has to happen inside the browser, " +
          "because Windows cannot separate one tab's audio from another's.\n\n" +
          "Load the extension from the extension folder via chrome://extensions, Developer mode, Load unpacked.",
          "Open extension folder");
        return;
      }

      List<Source> items = VisibleItems();
      if (items.Count == 0) {
        string headline = _tab == "screens" ? "No displays were detected."
                        : _tab == "windows" ? "No open windows were found."
                        : "Connected, but no recordable tabs were reported.";
        RestoreAndMessage(g, state, headline, null, null);
        return;
      }

      foreach (Source s in items) {
        Rectangle r;
        if (!_cardRects.TryGetValue(s.Id, out r)) continue;
        if (r.Bottom - ScrollY < 0 || r.Top - ScrollY > BodyRect.Height) continue;

        var screenRect = new Rectangle(r.X + BodyRect.X, r.Y + BodyRect.Y - ScrollY, r.Width, r.Height);
        AddHit("card:" + s.Id, screenRect, true, null);
        DrawCard(g, s, r);
      }

      g.Restore(state);
      DrawScrollbar(g);
    }

    void RestoreAndMessage(Graphics g, System.Drawing.Drawing2D.GraphicsState state,
                           string headline, string detail, string action) {
      g.Restore(state);

      var box = new RectangleF(BodyRect.X + Dpi.S(40), BodyRect.Y + Dpi.S(60),
                               BodyRect.Width - Dpi.S(80), BodyRect.Height - Dpi.S(80));

      using (var format = new StringFormat(StringFormat.GenericTypographic)) {
        format.Alignment = StringAlignment.Center;
        Theme.Text(g, headline, Theme.Get(Dpi.S(13f), FontStyle.Bold), Theme.FgDim,
                   new RectangleF(box.X, box.Y, box.Width, Dpi.S(22)), format);

        if (detail != null) {
          format.FormatFlags &= ~StringFormatFlags.NoWrap;
          using (var b = new SolidBrush(Theme.FgFaint))
            g.DrawString(detail, Theme.Get(Dpi.S(12.5f), FontStyle.Regular), b,
                         new RectangleF(box.X, box.Y + Dpi.S(30), box.Width, box.Height), format);
        }
      }

      if (action != null) {
        int w = Dpi.S(180), h = Dpi.S(32);
        var r = new Rectangle((int)(box.X + (box.Width - w) / 2), (int)(box.Y + Dpi.S(140)), w, h);
        AddHit("extfolder", r, true, null);
        Widgets.DrawButton(g, r, action, Widgets.ButtonStyle.Normal,
                            Hovered == "extfolder", PressedId == "extfolder", true, Dpi);
      }
    }

    void DrawCard(Graphics g, Source s, Rectangle r) {
      bool selected = _chosen != null && _chosen.Id == s.Id;
      bool hovered = Hovered == "card:" + s.Id;

      Color border = selected ? Theme.Accent : (hovered ? Theme.BorderStrong : Theme.Border);
      Color fill = selected ? Theme.AccentDim : (hovered ? Theme.BgRaise : Theme.BgPanel);
      Theme.FillAndStroke(g, r, Dpi.S((float)Theme.Radius), fill, border, Dpi.S(1.5f));

      int bodyH = Dpi.S(40);
      var thumb = new Rectangle(r.X, r.Y, r.Width, r.Height - bodyH);

      using (var clip = Theme.RoundedRect(r, Dpi.S((float)Theme.Radius))) {
        var state = g.Save();
        g.SetClip(clip);
        using (var b = new SolidBrush(Color.FromArgb(0x0d, 0x0f, 0x12)))
          g.FillRectangle(b, thumb);

        if (s.Thumbnail != null) {
          // object-fit: contain
          double scale = Math.Min((double)thumb.Width / s.Thumbnail.Width,
                                  (double)thumb.Height / s.Thumbnail.Height);
          int w = Math.Max(1, (int)(s.Thumbnail.Width * scale));
          int h = Math.Max(1, (int)(s.Thumbnail.Height * scale));
          g.DrawImage(s.Thumbnail, thumb.X + (thumb.Width - w) / 2, thumb.Y + (thumb.Height - h) / 2, w, h);
        } else {
          Theme.TextCentred(g, "No preview", Theme.Get(Dpi.S(11f), FontStyle.Regular), Theme.FgFaint, thumb);
        }
        g.Restore(state);
      }

      using (var pen = new Pen(Theme.Border, 1f))
        g.DrawLine(pen, thumb.X, thumb.Bottom, thumb.Right, thumb.Bottom);

      int x = r.X + Dpi.S(10);
      int textRight = r.Right - Dpi.S(10);

      if (s.Icon != null) {
        int size = Dpi.S(16);
        g.DrawImage(s.Icon, x, thumb.Bottom + (bodyH - size) / 2, size, size);
        x += size + Dpi.S(8);
      }

      if (s.Audible) {
        string badgeText = "audio";
        Font badgeFont = Theme.Get(Dpi.S(10f), FontStyle.Regular);
        int bw = (int)Theme.MeasureWidth(g, badgeText, badgeFont) + Dpi.S(12);
        var badge = new Rectangle(textRight - bw, thumb.Bottom + (bodyH - Dpi.S(16)) / 2, bw, Dpi.S(16));
        Theme.FillRounded(g, badge, badge.Height / 2f, Color.FromArgb(41, Theme.Ok.R, Theme.Ok.G, Theme.Ok.B));
        Theme.TextCentred(g, badgeText, badgeFont, Theme.Ok, badge);
        textRight = badge.X - Dpi.S(8);
      }

      string meta = s.Kind == "screen" && s.Width > 0 ? s.Width + " × " + s.Height
                  : s.Kind == "tab" ? s.Url : null;

      int width = Math.Max(Dpi.S(20), textRight - x);
      if (string.IsNullOrEmpty(meta)) {
        Theme.Text(g, s.Name, Theme.Get(Dpi.S(12f), FontStyle.Bold), Theme.Fg,
                   new RectangleF(x, thumb.Bottom, width, bodyH));
      } else {
        float lineH = Dpi.S(15f);
        float top = thumb.Bottom + (bodyH - lineH * 2) / 2f;
        Theme.Text(g, s.Name, Theme.Get(Dpi.S(12f), FontStyle.Bold), Theme.Fg,
                   new RectangleF(x, top, width, lineH));
        Theme.Text(g, meta, Theme.Get(Dpi.S(10.5f), FontStyle.Regular), Theme.FgFaint,
                   new RectangleF(x, top + lineH, width, lineH));
      }
    }

    void DrawFooter(Graphics g) {
      int h = Dpi.S(FooterH);
      var r = new Rectangle(0, Height - h, Width, h);

      using (var b = new SolidBrush(Theme.BgPanel)) g.FillRectangle(b, r);
      using (var pen = new Pen(Theme.Border, 1f)) g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);

      string label = _chosen != null ? _chosen.Label : "Nothing selected";
      Theme.Text(g, label, Theme.Get(Dpi.S(12f), FontStyle.Regular), Theme.FgDim,
                 new RectangleF(Dpi.S(16), r.Y, Dpi.S(320), h));

      int bh = Dpi.S(32);
      int y = r.Y + (h - bh) / 2;
      int x = Width - Dpi.S(16);

      int recordW = Dpi.S(112);
      x -= recordW;
      var record = new Rectangle(x, y, recordW, bh);
      AddHit("record", record, _chosen != null, null);
      x -= Dpi.S(8);

      int useW = Dpi.S(126);
      x -= useW;
      var use = new Rectangle(x, y, useW, bh);
      AddHit("use", use, _chosen != null, null);
      x -= Dpi.S(8);

      int cancelW = Dpi.S(76);
      x -= cancelW;
      var cancel = new Rectangle(x, y, cancelW, bh);
      AddHit("cancel", cancel, true, null);

      Widgets.DrawButton(g, cancel, "Cancel", Widgets.ButtonStyle.Normal,
                          Hovered == "cancel", PressedId == "cancel", true, Dpi);
      Widgets.DrawButton(g, use, "Use this source", Widgets.ButtonStyle.Primary,
                          Hovered == "use", PressedId == "use", _chosen != null, Dpi);
      Widgets.DrawButton(g, record, "Record now", Widgets.ButtonStyle.Primary,
                          Hovered == "record", PressedId == "record", _chosen != null, Dpi);
    }

    // --------------------------------------------------------------- actions

    protected override void OnHit(Hit hit) {
      if (hit.Id == "close" || hit.Id == "cancel") { Close(); return; }
      if (hit.Id == "refresh") { LoadSources(); return; }
      if (hit.Id == "extfolder") { _app.OpenExtensionFolder(); return; }

      if (hit.Id.StartsWith("tab:", StringComparison.Ordinal)) {
        _tab = hit.Id.Substring(4);
        ScrollY = 0;
        Relayout();
        Invalidate();
        return;
      }

      if (hit.Id.StartsWith("card:", StringComparison.Ordinal)) {
        string id = hit.Id.Substring(5);
        foreach (Source s in VisibleItems()) {
          if (s.Id != id) continue;
          _chosen = s;
          break;
        }
        Invalidate();
        return;
      }

      if (hit.Id == "use") { Apply(); Close(); return; }
      if (hit.Id == "record") { Apply(); Close(); _app.StartRecording(); return; }
    }

    void Apply() {
      if (_chosen == null) return;
      // The picker's copy owns bitmaps that die with this window, so the app
      // gets a detached one.
      var s = new Source();
      s.Kind = _chosen.Kind;
      s.Id = _chosen.Id;
      s.Name = _chosen.Name;
      s.OutputIdx = _chosen.OutputIdx;
      s.Width = _chosen.Width;
      s.Height = _chosen.Height;
      s.Bounds = _chosen.Bounds;
      s.Primary = _chosen.Primary;
      s.Handle = _chosen.Handle;
      s.Title = _chosen.Title;
      s.TabId = _chosen.TabId;
      s.Url = _chosen.Url;

      _app.CurrentSource = s;
      _app.NotifyState();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Escape) Close();
      else if (e.KeyCode == Keys.Enter && _chosen != null) { Apply(); Close(); _app.StartRecording(); }
      else if (e.KeyCode == Keys.F5) LoadSources();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
      base.OnFormClosed(e);
      DisposeAll(_screens);
      DisposeAll(_windows);
      _screens.Clear();
      _windows.Clear();
    }
  }
}
