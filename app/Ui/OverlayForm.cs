using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// The always-on-top control pill.
  ///
  /// Geometry is copied from overlay.css so it lands where it always did: a
  /// 412x54 box whose 46px pill sits 4px inside it, anchored by its right edge
  /// so collapsing pulls the left side in rather than sliding the whole thing
  /// across the screen.
  ///
  /// The window itself is larger than the pill, because a drop shadow that used
  /// to spill outside the element now has to fit inside the bitmap we hand to
  /// the compositor. That padding is invisible and click-through by virtue of
  /// being fully transparent.
  /// </summary>
  internal class OverlayForm : LayeredWindow {

    // Layout, in CSS pixels. Everything is scaled through Dpi.S on use.
    public const int AnchorFullW = 412;
    public const int AnchorCompactW = 229;     // time, pause, stop, chevron
    public const int AnchorH = 54;
    const int PillInset = 4;
    const int PillH = 46;
    const int PillRadius = 23;
    const int ShadowPad = 22;

    const int Btn = 30, Gap = 3, DivW = 1, DivMargin = 3, DivH = 22;
    const int PadLeft = 10, PadLeftCompact = 12, PadRight = 6;
    const int Dot = 9;

    readonly App _app;
    readonly Timer _ticker;
    readonly Stopwatch _animation = Stopwatch.StartNew();

    /// <summary>Anchor position always describes where the full-width pill
    /// would sit, so the two modes cannot drift apart.</summary>
    Point _anchor;
    bool _hasAnchor;
    bool _repositioning;

    Rectangle _pill;
    Rectangle _statusRect;
    string _savedName;

    // Cached strings. The layout pass runs on every frame, and while recording
    // that is eight times a second; the tooltips depend only on the settings
    // and the two status lines only change once a second, so rebuilding them
    // per frame is pure garbage.
    string _tipHide, _tipSettings, _tipMixer, _tipSys, _tipMic, _tipSource, _tipStart, _tipStop;
    string _tipPause, _tipResume;
    long _cachedSeconds = -1;
    string _cachedTime = "";
    long _cachedBytes = -1;
    string _cachedSub = "";
    string _cachedSubLabel;
    bool _cachedPaused;

    public OverlayForm(App app) {
      _app = app;
      NoActivate = true;
      Text = "Light Recorder";

      _ticker = new Timer();
      // Eight times a second drives both the elapsed clock and the pulsing
      // dot. The surface is reused, so a tick costs a redraw and no allocation.
      _ticker.Interval = 125;
      _ticker.Tick += delegate { Redraw(); };

      RefreshTooltips();
      ApplyLayout();
    }

    // ------------------------------------------------------------ placement

    Rectangle WorkAreaFor(Point p) {
      Screen s = Screen.FromPoint(p);
      return s.WorkingArea;
    }

    /// <summary>Where the full-width anchor should sit, clamped onto a display
    /// that currently exists - monitors get unplugged.</summary>
    Point AnchorPosition() {
      Settings s = Settings.Current;
      int w = Dpi.S(AnchorFullW), h = Dpi.S(AnchorH);

      if (s.HasCustomPos) {
        var stored = new Point(s.OverlayPosX, s.OverlayPosY);
        Rectangle wa = WorkAreaFor(stored);
        return new Point(
          Math.Min(Math.Max(stored.X, wa.X), wa.Right - w),
          Math.Min(Math.Max(stored.Y, wa.Y), wa.Bottom - h));
      }

      Rectangle primary = Screen.PrimaryScreen.WorkingArea;
      return new Point(
        primary.Right - w - Dpi.S(s.OverlayOffsetX),
        primary.Y + Dpi.S(s.OverlayOffsetY));
    }

    int AnchorWidth() {
      return Dpi.S(_app.OverlayCompact ? AnchorCompactW : AnchorFullW);
    }

    /// <summary>
    /// Resize and reposition for the current mode, keeping the right edge
    /// where it was.
    /// </summary>
    public void ApplyLayout() {
      if (!_hasAnchor) { _anchor = AnchorPosition(); _hasAnchor = true; }

      int fullW = Dpi.S(AnchorFullW);
      int w = AnchorWidth();
      int h = Dpi.S(AnchorH);
      int pad = Dpi.S(ShadowPad);

      // The anchor describes the full-width pill; a narrower one hangs off the
      // same right edge.
      int anchorX = _anchor.X + (fullW - w);

      _repositioning = true;
      try {
        SetBounds(anchorX - pad, _anchor.Y - pad, w + pad * 2, h + pad * 2);
      } finally {
        _repositioning = false;
      }
      Redraw();
    }

    protected override void OnMove(EventArgs e) {
      base.OnMove(e);
      if (_repositioning || !IsHandleCreated) return;

      // The user dragged us. Normalise back to where the full-width pill would
      // sit so switching modes does not shift the window.
      int pad = Dpi.S(ShadowPad);
      int fullW = Dpi.S(AnchorFullW);
      int w = AnchorWidth();

      _anchor = new Point(Left + pad - (fullW - w), Top + pad);

      Settings s = Settings.Current;
      s.HasCustomPos = true;
      s.OverlayPosX = _anchor.X;
      s.OverlayPosY = _anchor.Y;
      s.Save();

      _app.RepositionMixer();
    }

    protected override void OnDragFinished() {
      _app.RepositionMixer();
    }

    protected override void OnDpiChanged() {
      // Keep the same corner of the screen rather than the same pixel.
      _hasAnchor = false;
      ApplyLayout();
    }

    /// <summary>Screen rectangle of the visible pill, so the mixer can hang
    /// off its bottom-right corner.</summary>
    public Rectangle PillScreenBounds {
      get {
        int pad = Dpi.S(ShadowPad);
        int inset = Dpi.S(PillInset);
        int w = AnchorWidth();
        return new Rectangle(Left + pad + inset, Top + pad + inset,
                             w - inset * 2, Dpi.S(PillH));
      }
    }

    // -------------------------------------------------------------- state

    public void OnStateChanged() {
      bool recording = _app.IsRecording;
      // Paused, nothing moves - the clock stands still and so does the dot -
      // so there is nothing to redraw eight times a second.
      bool animating = recording && !_app.IsPaused;
      if (animating && !_ticker.Enabled) { _animation.Restart(); _ticker.Start(); }
      else if (!animating && _ticker.Enabled) _ticker.Stop();
      // Cleared when the next take begins, not the moment this one ends -
      // otherwise the state push that follows a stop wipes "Saved" off the
      // pill before anybody sees it.
      if (recording) _savedName = null;
      RefreshTooltips();
      Redraw();
    }

    public void ShowSaved(string file) {
      _savedName = System.IO.Path.GetFileName(file);
      Redraw();
    }

    /// <summary>Back to showing the source. Called when the overlay is put
    /// away, so bringing it up again answers "what will I record?" rather than
    /// "what did I record last time?".</summary>
    public void ClearSaved() {
      if (_savedName == null) return;
      _savedName = null;
      Redraw();
    }

    /// <summary>Rebuilt when the settings change, not when a frame is drawn.</summary>
    void RefreshTooltips() {
      Settings s = Settings.Current;
      bool sysMuted = !s.SystemAudioEnabled || s.SystemAudioMuted;
      bool micMuted = !s.MicEnabled || s.MicMuted;
      string micKey = HotkeyManager.Display(s.Hotkeys.ToggleMic);

      _tipHide = "Hide overlay (" + HotkeyManager.Display(s.Hotkeys.ToggleOverlay) + ")";
      _tipSettings = "Settings";
      _tipMixer = "Per-app and per-tab audio";
      _tipSys = sysMuted ? "System audio muted - click to unmute"
                         : "System audio recording - click to mute";
      _tipMic = micMuted ? "Microphone muted - click to unmute (" + micKey + ")"
                         : "Microphone live - click to mute (" + micKey + ")";
      _tipSource = "Choose what to record (" + HotkeyManager.Display(s.Hotkeys.Picker) + ")";
      _tipStart = "Start recording (" + HotkeyManager.Display(s.Hotkeys.StartStop) + ")";
      _tipStop = "Stop recording (" + HotkeyManager.Display(s.Hotkeys.Stop) + ")";
      string pauseKey = HotkeyManager.Display(s.Hotkeys.TogglePause);
      _tipPause = "Pause recording (" + pauseKey + ")";
      _tipResume = "Resume recording (" + pauseKey + ")";
    }

    // ------------------------------------------------------------- layout

    protected override void Arrange(Size size) {
      int pad = Dpi.S(ShadowPad);
      int inset = Dpi.S(PillInset);
      bool compact = _app.OverlayCompact;

      _pill = new Rectangle(pad + inset, pad + inset,
                            AnchorWidth() - inset * 2, Dpi.S(PillH));

      int btn = Dpi.S(Btn), gap = Dpi.S(Gap);
      int padL = Dpi.S(compact ? PadLeftCompact : PadLeft);
      int padR = Dpi.S(PadRight);
      int y = _pill.Y + (_pill.Height - btn) / 2;

      bool recording = _app.IsRecording;

      // Laid out from the right, so the status block absorbs the slack exactly
      // as flex:1 did.
      int x = _pill.Right - padR;

      if (!compact) {
        x -= btn; AddHit("hide", new Rectangle(x, y, btn, btn), true, _tipHide);
        x -= gap;
      }

      if (recording) {
        x -= btn;
        AddHit("collapse", new Rectangle(x, y, btn, btn), true, compact ? "Expand" : "Collapse");
        x -= gap;
      }

      if (!compact) {
        x -= btn; AddHit("settings", new Rectangle(x, y, btn, btn), true, _tipSettings); x -= gap;
        x -= btn; AddHit("mixer", new Rectangle(x, y, btn, btn), true, _tipMixer); x -= gap;
        x -= btn; AddHit("sys", new Rectangle(x, y, btn, btn), true, _tipSys); x -= gap;
        x -= btn; AddHit("mic", new Rectangle(x, y, btn, btn), true, _tipMic); x -= gap;
        x -= btn; AddHit("source", new Rectangle(x, y, btn, btn), true, _tipSource); x -= gap;

        x -= Dpi.S(DivW) + Dpi.S(DivMargin) * 2;
        AddDivider(x + Dpi.S(DivMargin));
        x -= gap;
      }

      x -= btn;
      AddHit("stop", new Rectangle(x, y, btn, btn), recording, _tipStop);
      x -= gap;

      // One slot, three jobs: record when idle, pause while recording, resume
      // while paused. Besides stop it is the only control the collapsed pill
      // keeps, which is why it is the one that does the most.
      x -= btn;
      if (recording) AddHit("pause", new Rectangle(x, y, btn, btn), true, _app.IsPaused ? _tipResume : _tipPause);
      else AddHit("start", new Rectangle(x, y, btn, btn), true, _tipStart);
      x -= gap;

      x -= Dpi.S(DivW) + Dpi.S(DivMargin) * 2;
      AddDivider(x + Dpi.S(DivMargin));

      int statusLeft = _pill.X + padL;
      _statusRect = new Rectangle(statusLeft, _pill.Y, Math.Max(Dpi.S(24), x - statusLeft), _pill.Height);

      // The status block doubles as the drag handle, exactly as the CSS made
      // the whole pill draggable except for its buttons.
      DragRegion = new Rectangle(_pill.X, _pill.Y, _pill.Width, _pill.Height);
    }

    readonly System.Collections.Generic.List<Rectangle> _dividers = new System.Collections.Generic.List<Rectangle>();

    void AddDivider(int x) {
      _dividers.Add(new Rectangle(x, _pill.Y + (_pill.Height - Dpi.S(DivH)) / 2, Dpi.S(DivW), Dpi.S(DivH)));
    }

    // ------------------------------------------------------------ rendering

    protected override void OnRender(Graphics g, Size size) {
      bool recording = _app.IsRecording;
      bool starting = _app.IsStarting;
      bool compact = _app.OverlayCompact;

      // Shadow first, offset 8px down as box-shadow: 0 8px 28px rgba(0,0,0,.55)
      Bitmap shadow = Theme.Shadow(_pill.Width, _pill.Height, Dpi.S((float)PillRadius), Dpi.S(28), 140);
      if (shadow != null) {
        int blur = Dpi.S(28);
        g.DrawImageUnscaled(shadow, _pill.X - blur * 2, _pill.Y - blur * 2 + Dpi.S(8));
      }

      Theme.FillAndStroke(g, _pill, Dpi.S((float)PillRadius), Theme.Glass, Theme.BorderStrong, 1f);

      foreach (Rectangle d in _dividers) {
        using (var b = new SolidBrush(Theme.Border)) g.FillRectangle(b, d);
      }
      _dividers.Clear();

      DrawStatus(g, recording, starting, compact);

      foreach (Hit h in Hits) DrawButton(g, h, recording);
    }

    void DrawStatus(Graphics g, bool recording, bool starting, bool compact) {
      int dot = Dpi.S(Dot);
      var dotRect = new Rectangle(_statusRect.X,
                                  _statusRect.Y + (_statusRect.Height - dot) / 2, dot, dot);

      bool paused = recording && _app.IsPaused;
      Color dotColour = Theme.FgFaint;
      if (starting || paused) dotColour = Theme.Warn;
      else if (recording) dotColour = Theme.Rec;

      if ((recording || starting) && !paused) {
        // pulse 1.6s ease-in-out infinite, dipping to 25% opacity.
        double period = starting ? 0.8 : 1.6;
        double phase = (_animation.Elapsed.TotalSeconds % period) / period;
        double k = 0.5 + 0.5 * Math.Cos(phase * 2 * Math.PI);
        int alpha = (int)(255 * (0.25 + 0.75 * k));
        dotColour = Theme.WithAlpha(dotColour, Math.Max(0, Math.Min(255, alpha)));
      }

      using (var b = new SolidBrush(dotColour)) g.FillEllipse(b, dotRect);

      int textLeft = dotRect.Right + Dpi.S(8);
      int textWidth = Math.Max(Dpi.S(10), _statusRect.Right - textLeft);

      string primary;
      Color primaryColour = Theme.Fg;
      if (_savedName != null) {
        primary = "Saved";
      } else if (recording) {
        primary = TimeText(_app.ElapsedMs);
        primaryColour = paused ? Theme.Warn : Theme.Rec;
      } else {
        primary = "Ready";
      }

      if (compact) {
        Font f = Theme.Get(Dpi.S(14f), FontStyle.Bold);
        Theme.Text(g, primary, f, primaryColour,
                   new RectangleF(textLeft, _statusRect.Y, textWidth, _statusRect.Height));
        return;
      }

      string secondary = _savedName ?? SubLine();

      Font primaryFont = Theme.Get(Dpi.S(13f), FontStyle.Bold);
      Font secondaryFont = Theme.Get(Dpi.S(10.5f), FontStyle.Regular);

      float lineH = Dpi.S(16f);
      float top = _statusRect.Y + (_statusRect.Height - lineH * 2) / 2f;

      Theme.Text(g, primary, primaryFont, primaryColour,
                 new RectangleF(textLeft, top, textWidth, lineH));
      Theme.Text(g, secondary, secondaryFont, Theme.FgFaint,
                 new RectangleF(textLeft, top + lineH, textWidth, lineH));
    }

    /// <summary>Formatted elapsed time, rebuilt only when the second ticks
    /// over rather than on all eight frames within it.</summary>
    string TimeText(long ms) {
      long seconds = ms / 1000;
      if (seconds != _cachedSeconds) {
        _cachedSeconds = seconds;
        _cachedTime = FormatDuration(ms);
      }
      return _cachedTime;
    }

    string SubLine() {
      string label = _app.CurrentSource != null ? _app.CurrentSource.Label : "Entire Screen";
      long bytes = _app.ProgressBytes;
      bool paused = _app.IsPaused;
      if (!_app.IsRecording || (bytes <= 0 && !paused)) return label;

      // The byte count only moves when a fragment is written, so this is
      // rebuilt that often at most, no matter how fast the overlay repaints.
      if (bytes != _cachedBytes || paused != _cachedPaused || !ReferenceEquals(label, _cachedSubLabel)) {
        _cachedBytes = bytes;
        _cachedPaused = paused;
        _cachedSubLabel = label;
        _cachedSub = (paused ? "Paused" : FormatBytes(bytes)) + " · " + label;
      }
      return _cachedSub;
    }

    void DrawButton(Graphics g, Hit h, bool recording) {
      string icon;
      bool muted = false;
      Color colour = Theme.FgDim;

      Settings s = Settings.Current;
      switch (h.Id) {
        case "start":
          icon = Icons.Record;
          colour = h.Enabled ? Theme.Rec : Theme.FgFaint;
          break;
        case "stop": icon = Icons.Stop; break;
        case "pause":
          // Resuming is recording again, so it wears the record button's dot.
          if (_app.IsPaused) { icon = Icons.Record; colour = Theme.Rec; }
          else icon = Icons.Pause;
          break;
        case "source": icon = Icons.Monitor; break;
        case "mic":
          icon = Icons.Mic;
          muted = !s.MicEnabled || s.MicMuted;
          if (muted) colour = Theme.Rec;
          break;
        case "sys":
          icon = Icons.Speaker;
          muted = !s.SystemAudioEnabled || s.SystemAudioMuted;
          if (muted) colour = Theme.Rec;
          break;
        case "mixer": icon = Icons.Mixer; break;
        case "settings": icon = Icons.Settings; break;
        case "collapse": icon = Icons.Chevron; break;
        case "hide": icon = Icons.Close; break;
        default: return;
      }

      bool hovered = Hovered == h.Id && h.Enabled;
      bool pressed = Pressed == h.Id && h.Enabled;

      bool recTinted = h.Id == "start" || (h.Id == "pause" && _app.IsPaused);
      if (hovered) {
        Color bg = recTinted ? Theme.WithAlpha(Theme.Rec, 41) : Theme.BgHover;
        Theme.FillRounded(g, h.Bounds, Dpi.S((float)Theme.RadiusSm), bg);
        if (!recTinted && !muted) colour = Theme.Fg;
      }

      if (!h.Enabled) colour = Theme.WithAlpha(Theme.FgFaint, 77);   // opacity .3

      Rectangle iconBox = h.Bounds;
      if (pressed) iconBox.Offset(0, Math.Max(1, Dpi.S(1)));

      // The chevron flips rather than swapping icons, so the two states read as
      // one control.
      if (h.Id == "collapse" && !_app.OverlayCompact) {
        var state = g.Save();
        g.TranslateTransform(iconBox.X + iconBox.Width / 2f, iconBox.Y + iconBox.Height / 2f);
        g.RotateTransform(180);
        g.TranslateTransform(-(iconBox.X + iconBox.Width / 2f), -(iconBox.Y + iconBox.Height / 2f));
        Icons.Draw(g, icon, InnerIcon(iconBox), colour, false);
        g.Restore(state);
        return;
      }

      Icons.Draw(g, icon, InnerIcon(iconBox), colour, muted);
    }

    RectangleF InnerIcon(Rectangle button) {
      float size = Dpi.S(17f);
      return new RectangleF(button.X + (button.Width - size) / 2f,
                            button.Y + (button.Height - size) / 2f, size, size);
    }

    // -------------------------------------------------------------- actions

    protected override void OnHit(Hit hit) {
      switch (hit.Id) {
        case "start": _app.StartRecording(); break;
        case "stop": _app.StopRecording(); break;
        case "pause": _app.TogglePause(); break;
        case "source": _app.OpenPicker(); break;
        case "mic": _app.ToggleMic(); break;
        case "sys": _app.ToggleSystemAudio(); break;
        case "mixer": _app.ToggleMixer(); break;
        case "settings": _app.OpenSettings(); break;
        case "collapse": _app.SetCompact(!_app.OverlayCompact); break;
        case "hide": _app.HideOverlay(); break;
      }
    }

    // ------------------------------------------------------------- helpers

    public static string FormatDuration(long ms) {
      long total = Math.Max(0, ms / 1000);
      long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
      return h > 0
        ? h + ":" + m.ToString("00") + ":" + s.ToString("00")
        : m.ToString("00") + ":" + s.ToString("00");
    }

    public static string FormatBytes(long bytes) {
      if (bytes <= 0) return "0 MB";
      string[] units = { "B", "KB", "MB", "GB", "TB" };
      double n = bytes;
      int i = 0;
      while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
      return (n < 10 && i > 1 ? n.ToString("0.0") : Math.Round(n).ToString("0")) + " " + units[i];
    }

    protected override void Dispose(bool disposing) {
      if (disposing && _ticker != null) { _ticker.Stop(); _ticker.Dispose(); }
      base.Dispose(disposing);
    }
  }
}
