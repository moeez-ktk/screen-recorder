using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace LightRecorder.Ui {

  /// <summary>
  /// Settings. Every change saves immediately - there is no Apply button,
  /// exactly as before.
  ///
  /// Laid out in immediate mode: one pass walks down the page computing
  /// rectangles, registering hit regions and drawing. That keeps the whole
  /// window in one readable file and means there is no widget tree, no HWND per
  /// control, and nothing to keep in sync with the settings object.
  /// </summary>
  internal class SettingsForm : PaintedForm {

    const int FooterH = 56;
    const int PagePadX = 18;

    readonly App _app;

    TextBox _filename;
    TextBox _port;

    List<AudioDeviceInfo> _mics = new List<AudioDeviceInfo>();
    string _capturing;                    // hotkey row currently listening
    int _layoutY;                         // running cursor during a layout pass
    bool _measuring;

    public SettingsForm(App app) {
      _app = app;
      TitleText = "Settings";
      CentreOnCursor(640, 760);

      // Saving on every keystroke would rewrite settings.json - and repaint
      // every open window through the change notification - once per character.
      _filenameSave = new System.Windows.Forms.Timer();
      _filenameSave.Interval = 400;
      _filenameSave.Tick += delegate {
        _filenameSave.Stop();
        if (Settings.Current.FilenamePattern == _filename.Text) return;
        Settings.Current.FilenamePattern = _filename.Text;
        Settings.Current.Save();
      };

      _filename = Widgets.MakeTextBox(false, Dpi);
      _filename.Text = Settings.Current.FilenamePattern;
      _filename.TextChanged += delegate { _filenameSave.Stop(); _filenameSave.Start(); };
      _filename.Leave += delegate { _filenameSave.Stop(); CommitFilename(); };
      Controls_Add(_filename);

      _port = Widgets.MakeTextBox(false, Dpi);
      _port.Text = Settings.Current.BridgePort.ToString(CultureInfo.InvariantCulture);
      _port.Leave += delegate { CommitPort(); };
      Controls_Add(_port);

      LoadMicrophones();
    }

    System.Windows.Forms.Timer _filenameSave;

    void Controls_Add(Control c) {
      c.Visible = false;
      Controls.Add(c);
    }

    void CommitFilename() {
      if (Settings.Current.FilenamePattern == _filename.Text) return;
      Settings.Current.FilenamePattern = _filename.Text;
      Settings.Current.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
      // Whatever was typed but not yet flushed still counts.
      if (_filenameSave != null) _filenameSave.Stop();
      CommitFilename();
      CommitPort();
      base.OnFormClosing(e);
    }

    void CommitPort() {
      int value;
      if (!int.TryParse(_port.Text, out value) || value < 1024 || value > 65535) {
        _port.Text = Settings.Current.BridgePort.ToString(CultureInfo.InvariantCulture);
        return;
      }
      if (value == Settings.Current.BridgePort) return;
      Settings.Current.BridgePort = value;
      Settings.Current.Save();
      _app.RestartBridge(value);
    }

    void LoadMicrophones() {
      ThreadPool.QueueUserWorkItem(delegate {
        List<AudioDeviceInfo> list = AudioDevices.ListMicrophones();
        try {
          if (!IsHandleCreated || IsDisposed) return;
          BeginInvoke((MethodInvoker)delegate {
            if (IsDisposed) return;
            _mics = list;
            Invalidate();
          });
        } catch (Exception) { }
      });
    }

    public void OnStateChanged() { Invalidate(); }

    // ---------------------------------------------------------------- layout

    protected override void Relayout() {
      BodyRect = new Rectangle(0, Dpi.S(TitleBarHeight), Width,
                               Height - Dpi.S(TitleBarHeight) - Dpi.S(FooterH));
      Measure();
    }

    /// <summary>Run the page once without drawing, to learn its height.</summary>
    void Measure() {
      using (var probe = new Bitmap(1, 1))
      using (Graphics g = Graphics.FromImage(probe)) {
        Theme.Prepare(g);
        _measuring = true;
        try { Page(g); } finally { _measuring = false; }
        ContentHeight = _layoutY + Dpi.S(18);
      }
      if (ScrollY > Math.Max(0, ContentHeight - BodyRect.Height))
        ScrollY = Math.Max(0, ContentHeight - BodyRect.Height);
    }

    protected override void OnScrolled() { PositionTextBoxes(); }

    protected override void OnRender(Graphics g) {
      if (BodyRect.Width == 0) Relayout();

      DrawTitleBar(g);
      Hit close = AddTitleHit();
      DrawIconButton(g, close, Icons.Close, null);

      var state = g.Save();
      g.SetClip(BodyRect);
      g.TranslateTransform(0, BodyRect.Y - ScrollY);
      Page(g);
      g.Restore(state);

      DrawScrollbar(g);
      DrawFooter(g);
      PositionTextBoxes();
    }

    Hit AddTitleHit() {
      int h = Dpi.S(TitleBarHeight);
      int btn = Dpi.S(30);
      return AddHit("close", new Rectangle(Width - Dpi.S(8) - btn, (h - btn) / 2, btn, btn), true, "Close");
    }

    // ------------------------------------------------------------ page body
    //
    // Everything below runs in both the measuring pass and the drawing pass.
    // Rect() advances the cursor; Visible() decides whether anything is worth
    // painting; Hit registration is skipped while measuring because the
    // rectangles would be wrong.

    Rectangle _filenameBox, _portBox;

    void Page(Graphics g) {
      _layoutY = Dpi.S(4);
      int left = Dpi.S(PagePadX);
      int width = Width - Dpi.S(PagePadX) * 2 - Dpi.S(10);

      Settings s = Settings.Current;

      // ------------------------------------------------------------ output
      Section(g, "Where recordings go", left, width);

      Label(g, "Save folder", left, width);
      Rectangle folderRow = Row(Dpi.S(32));
      int btnW = Dpi.S(84), openW = Dpi.S(62);
      var folderField = new Rectangle(left, folderRow.Y, width - btnW - openW - Dpi.S(16), folderRow.Height);
      DrawReadonlyField(g, folderField, s.OutputDir);
      Button(g, new Rectangle(folderField.Right + Dpi.S(8), folderRow.Y, btnW, folderRow.Height),
             "chooseFolder", "Change…", Widgets.ButtonStyle.Normal, true);
      Button(g, new Rectangle(folderField.Right + Dpi.S(16) + btnW, folderRow.Y, openW, folderRow.Height),
             "openFolder", "Open", Widgets.ButtonStyle.Normal, true);
      Gap(Dpi.S(14));

      Label(g, "File name", left, width);
      _filenameBox = Row(Dpi.S(32));
      _filenameBox = new Rectangle(left, _filenameBox.Y, width, _filenameBox.Height);
      if (!_measuring) Widgets.DrawFieldBox(g, _filenameBox, _filename.Focused, Dpi);
      Hint(g, "{date} and {time} are filled in. A number is appended if the name is already taken.", left, width);
      Gap(Dpi.S(10));

      SwitchRow(g, "remuxOnStop", s.RemuxOnStop, "Optimise for video editors when stopping",
                "Recordings are already valid MP4 files that survive a crash. This adds a fast stream-copy " +
                "pass so the file also seeks instantly in editors - it delays stopping by a few seconds on " +
                "long recordings.", left, width);

      // ------------------------------------------------------------- video
      Section(g, "Video", left, width);

      int half = (width - Dpi.S(14)) / 2;
      int rowTop = _layoutY;

      Label(g, "Frame rate", left, half);
      Select(g, "fps", left, half, s.Fps + " fps");
      int leftBottom = _layoutY;

      _layoutY = rowTop;
      int rightX = left + half + Dpi.S(14);
      Label(g, "Resolution", rightX, half);
      Select(g, "resolution", rightX, half, ResolutionLabel(s.Resolution));
      Hint(g, "Never upscales beyond the source.", rightX, half);

      _layoutY = Math.Max(leftBottom, _layoutY);
      Gap(Dpi.S(8));

      Label(g, "Encoder", left, width);
      Select(g, "encoder", left, width, EncoderLabel(s.Encoder));
      Hint(g, EncoderHint(), left, width);
      Gap(Dpi.S(10));

      rowTop = _layoutY;
      Label(g, "Quality mode", left, half);
      Select(g, "qualityMode", left, half,
             s.QualityMode == "bitrate" ? "Fixed bitrate" : "Constant quality");
      leftBottom = _layoutY;

      _layoutY = rowTop;
      if (s.QualityMode == "bitrate") {
        LabelWithValue(g, "Bitrate", s.BitrateMbps + " Mbps", rightX, half);
        Slider(g, "bitrateMbps", rightX, half, (s.BitrateMbps - 2) / 58.0);
      } else {
        LabelWithValue(g, "Quality", "CQ " + s.Quality, rightX, half);
        Slider(g, "quality", rightX, half, (s.Quality - 14) / 20.0);
        Hint(g, "Lower is better quality and a larger file.", rightX, half);
      }

      _layoutY = Math.Max(leftBottom, _layoutY);
      Gap(Dpi.S(10));

      SwitchRow(g, "captureCursor", s.CaptureCursor, "Include the mouse cursor", null, left, width);

      // ------------------------------------------------------------- audio
      Section(g, "Audio", left, width);

      SwitchRow(g, "systemAudioEnabled", s.SystemAudioEnabled, "Record system audio",
                "Everything playing on this PC: games, browser, music.", left, width);
      SwitchRow(g, "micEnabled", s.MicEnabled, "Record microphone", null, left, width);

      Label(g, "Microphone", left, width);
      Select(g, "micDeviceId", left, width, MicLabel(s.MicDeviceId));
      Gap(Dpi.S(10));
      SwitchRow(g, "micAutoLevel", s.MicAutoLevel, "Level the microphone automatically",
                "Lifts a quiet microphone to a clear speaking level and holds it there, the way call apps " +
                "and browser recorders do. The quiet between sentences is left alone.", left, width);

      rowTop = _layoutY;
      LabelWithValue(g, "System volume", Percent(s.SystemAudioGain), left, half);
      Slider(g, "systemAudioGain", left, half, s.SystemAudioGain / 2.0);
      leftBottom = _layoutY;

      _layoutY = rowTop;
      LabelWithValue(g, "Mic volume", Percent(s.MicGain), rightX, half);
      // To 400%: with levelling off, a quiet microphone can need more than
      // double.
      Slider(g, "micGain", rightX, half, s.MicGain / 4.0);
      _layoutY = Math.Max(leftBottom, _layoutY);
      Gap(Dpi.S(6));

      // --------------------------------------------------------- shortcuts
      Section(g, "Shortcuts", left, width);
      Hint(g, "Click a field and press the combination you want. These work from any application.", left, width);
      Gap(Dpi.S(10));

      for (int i = 0; i < Hotkeys.Names.Length; i++) {
        string name = Hotkeys.Names[i];
        Rectangle row = Row(Dpi.S(32));
        int fieldW = Dpi.S(190);

        if (!_measuring)
          Theme.Text(g, Hotkeys.Labels[i], Theme.Get(Dpi.S(12.5f), FontStyle.Regular), Theme.Fg,
                     new RectangleF(left, row.Y, width - fieldW - Dpi.S(10), row.Height));

        var field = new Rectangle(left + width - fieldW, row.Y, fieldW, row.Height);
        AddPageHit("hk:" + name, field, true, null);

        if (!_measuring) {
          bool capturing = _capturing == name;
          Theme.FillAndStroke(g, field, Dpi.S((float)Theme.RadiusSm),
                              capturing ? Theme.AccentDim : Theme.BgRaise,
                              capturing ? Theme.Accent : (Hovered == "hk:" + name ? Theme.BorderStrong : Theme.Border), 1f);
          string text = capturing ? "Press keys…" : (s.Hotkeys.Get(name) ?? "Not set");
          if (string.IsNullOrEmpty(text)) text = "Not set";
          Theme.TextCentred(g, HotkeyManager.Display(text), Theme.Mono(Dpi.S(12f), FontStyle.Regular),
                            capturing ? Theme.Accent : Theme.Fg, field);
        }
        Gap(Dpi.S(7));
      }

      Gap(Dpi.S(8));
      DrawHotkeyStatus(g, left, width);

      // ------------------------------------------------------ when you are done
      Section(g, "When you are done", left, width);

      LabelWithValue(g, "Hide the overlay after stopping",
                     s.AutoHideAfterStopSeconds == 0 ? "never" : s.AutoHideAfterStopSeconds + "s",
                     left, width);
      Slider(g, "autoHide", left, width, s.AutoHideAfterStopSeconds / 120.0);
      Hint(g, "Set to 0 to leave the overlay up until you dismiss it yourself.", left, width);
      Gap(Dpi.S(6));

      // ---------------------------------------------------------- advanced
      Section(g, "Advanced", left, width);

      Label(g, "ffmpeg", left, width);
      Rectangle ffRow = Row(Dpi.S(32));
      int locateW = Dpi.S(78), redetectW = Dpi.S(88);
      var ffField = new Rectangle(left, ffRow.Y, width - locateW - redetectW - Dpi.S(16), ffRow.Height);
      DrawReadonlyField(g, ffField, _app.EncoderInfo.Ffmpeg ?? "");
      Button(g, new Rectangle(ffField.Right + Dpi.S(8), ffRow.Y, locateW, ffRow.Height),
             "chooseFfmpeg", "Locate…", Widgets.ButtonStyle.Normal, true);
      Button(g, new Rectangle(ffField.Right + Dpi.S(16) + locateW, ffRow.Y, redetectW, ffRow.Height),
             "redetect", "Re-detect", Widgets.ButtonStyle.Normal, true);
      HintRich(g, FfmpegHint(), FfmpegHintGood(), left, width);
      Gap(Dpi.S(12));

      Label(g, "Browser extension port", left, width);
      Rectangle portRow = Row(Dpi.S(32));
      _portBox = new Rectangle(left, portRow.Y, Dpi.S(120), portRow.Height);
      if (!_measuring) Widgets.DrawFieldBox(g, _portBox, _port.Focused, Dpi);
      if (!_measuring)
        Theme.Text(g, _app.Bridge.IsConnected ? "Extension connected" : "Waiting for the browser extension…",
                   Theme.Get(Dpi.S(11f), FontStyle.Regular),
                   _app.Bridge.IsConnected ? Theme.Ok : Theme.FgFaint,
                   new RectangleF(_portBox.Right + Dpi.S(12), portRow.Y, width - _portBox.Width - Dpi.S(12), portRow.Height));
      Gap(Dpi.S(14));

      Label(g, "Extension pairing token", left, width);
      Rectangle tokenRow = Row(Dpi.S(32));
      int copyW = Dpi.S(62), extW = Dpi.S(96);
      var tokenField = new Rectangle(left, tokenRow.Y, width - copyW - extW - Dpi.S(16), tokenRow.Height);
      DrawReadonlyField(g, tokenField, _app.Bridge.Token, true);
      Button(g, new Rectangle(tokenField.Right + Dpi.S(8), tokenRow.Y, copyW, tokenRow.Height),
             "copyToken", "Copy", Widgets.ButtonStyle.Normal, true);
      Button(g, new Rectangle(tokenField.Right + Dpi.S(16) + copyW, tokenRow.Y, extW, tokenRow.Height),
             "extFolder", "Open folder", Widgets.ButtonStyle.Normal, true);
      Hint(g, "Paste this into the extension so only it can talk to the recorder.", left, width);
    }

    // ------------------------------------------------------- layout helpers

    Rectangle Row(int height) {
      var r = new Rectangle(0, _layoutY, Width, height);
      _layoutY += height;
      return r;
    }

    void Gap(int height) { _layoutY += height; }

    void Section(Graphics g, string title, int left, int width) {
      if (_layoutY > Dpi.S(8)) {
        _layoutY += Dpi.S(14);
        if (!_measuring) {
          using (var pen = new Pen(Theme.Border, 1f))
            g.DrawLine(pen, left, _layoutY, left + width, _layoutY);
        }
        _layoutY += Dpi.S(14);
      }
      Rectangle r = Row(Dpi.S(20));
      if (!_measuring)
        Widgets.DrawHeading(g, title, new RectangleF(left, r.Y, width, r.Height), Dpi);
      Gap(Dpi.S(8));
    }

    void Label(Graphics g, string text, int left, int width) {
      Rectangle r = Row(Dpi.S(18));
      if (!_measuring)
        Theme.Text(g, text, Theme.Get(Dpi.S(12f), FontStyle.Regular), Theme.FgDim,
                   new RectangleF(left, r.Y, width, r.Height));
      Gap(Dpi.S(5));
    }

    void LabelWithValue(Graphics g, string text, string value, int left, int width) {
      Rectangle r = Row(Dpi.S(18));
      if (!_measuring) {
        Theme.Text(g, text, Theme.Get(Dpi.S(12f), FontStyle.Regular), Theme.FgDim,
                   new RectangleF(left, r.Y, width, r.Height));
        Font vf = Theme.Get(Dpi.S(12f), FontStyle.Bold);
        float vw = Theme.MeasureWidth(g, value, vf);
        Theme.Text(g, value, vf, Theme.Accent,
                   new RectangleF(left + width - vw, r.Y, vw + Dpi.S(2), r.Height));
      }
      Gap(Dpi.S(5));
    }

    void Hint(Graphics g, string text, int left, int width) {
      if (string.IsNullOrEmpty(text)) return;
      Gap(Dpi.S(4));
      float h;
      Font font = Theme.Get(Dpi.S(11f), FontStyle.Regular);
      if (_measuring) {
        h = Theme.MeasureWrapped(g, text, font, width);
      } else {
        h = Theme.MeasureWrapped(g, text, font, width);
        Theme.WrappedText(g, text, font, Theme.FgFaint, new RectangleF(left, _layoutY, width, h + Dpi.S(4)));
      }
      _layoutY += (int)h;
    }

    /// <summary>A hint whose first clause is coloured to say good or bad, as
    /// the settings page used to do with a span.</summary>
    void HintRich(Graphics g, string text, bool good, int left, int width) {
      if (string.IsNullOrEmpty(text)) return;
      Gap(Dpi.S(4));
      Font font = Theme.Get(Dpi.S(11f), FontStyle.Regular);
      float h = Theme.MeasureWrapped(g, text, font, width);
      if (!_measuring) {
        int split = text.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0) {
          string head = text.Substring(0, split);
          float headW = Theme.MeasureWidth(g, head, font);
          Theme.Text(g, head, font, good ? Theme.Ok : Theme.Rec,
                     new RectangleF(left, _layoutY, headW + Dpi.S(2), Dpi.S(16)));
          Theme.WrappedText(g, text.Substring(split), font, Theme.FgFaint,
                            new RectangleF(left + headW, _layoutY, width - headW, h + Dpi.S(4)));
        } else {
          Theme.WrappedText(g, text, font, Theme.FgFaint, new RectangleF(left, _layoutY, width, h + Dpi.S(4)));
        }
      }
      _layoutY += (int)h;
    }

    void DrawReadonlyField(Graphics g, Rectangle r, string text) { DrawReadonlyField(g, r, text, false); }

    void DrawReadonlyField(Graphics g, Rectangle r, string text, bool mono) {
      if (_measuring) return;
      Widgets.DrawFieldBox(g, r, false, Dpi);
      Theme.Text(g, text, mono ? Theme.Mono(Dpi.S(12f), FontStyle.Regular) : Theme.Get(Dpi.S(13f), FontStyle.Regular),
                 Theme.FgDim, new RectangleF(r.X + Dpi.S(9), r.Y, r.Width - Dpi.S(18), r.Height));
    }

    void Button(Graphics g, Rectangle r, string id, string text, Widgets.ButtonStyle style, bool enabled) {
      AddPageHit(id, r, enabled, null);
      if (_measuring) return;
      Widgets.DrawButton(g, r, text, style, Hovered == id, PressedId == id, enabled, Dpi);
    }

    void Select(Graphics g, string id, int left, int width, string text) {
      Rectangle r = Row(Dpi.S(32));
      var box = new Rectangle(left, r.Y, width, r.Height);
      AddPageHit("select:" + id, box, true, null);
      if (!_measuring)
        Widgets.DrawSelect(g, box, text, Hovered == "select:" + id, _openSelect == id, Dpi);
      Gap(Dpi.S(4));
    }

    void Slider(Graphics g, string id, int left, int width, double value01) {
      Rectangle r = Row(Dpi.S(24));
      var box = new Rectangle(left, r.Y, width, r.Height);
      AddPageHit("slider:" + id, box, true, null);
      if (!_measuring)
        Widgets.DrawSlider(g, box, value01, Dpi, Hovered == "slider:" + id || PressedId == "slider:" + id);
      Gap(Dpi.S(4));
    }

    void SwitchRow(Graphics g, string id, bool on, string label, string hint, int left, int width) {
      int switchW = Dpi.S(34), switchH = Dpi.S(19);
      int textLeft = left + switchW + Dpi.S(10);
      int textWidth = width - switchW - Dpi.S(10);

      Font labelFont = Theme.Get(Dpi.S(12.5f), FontStyle.Regular);
      Font hintFont = Theme.Get(Dpi.S(11f), FontStyle.Regular);

      float hintH = string.IsNullOrEmpty(hint) ? 0 : Theme.MeasureWrapped(g, hint, hintFont, textWidth);
      int rowH = (int)Math.Max(Dpi.S(22), Dpi.S(20) + hintH + (hintH > 0 ? Dpi.S(4) : 0));

      Rectangle r = Row(rowH);
      var sw = new Rectangle(left, r.Y + Dpi.S(1), switchW, switchH);
      AddPageHit("switch:" + id, sw, true, null);

      if (!_measuring) {
        Widgets.DrawSwitch(g, sw, on, Dpi);
        Theme.Text(g, label, labelFont, Theme.Fg, new RectangleF(textLeft, r.Y, textWidth, Dpi.S(20)));
        if (hintH > 0)
          Theme.WrappedText(g, hint, hintFont, Theme.FgFaint,
                            new RectangleF(textLeft, r.Y + Dpi.S(20) + Dpi.S(2), textWidth, hintH + Dpi.S(4)));
      }
      Gap(Dpi.S(14));
    }

    void DrawHotkeyStatus(Graphics g, int left, int width) {
      bool autostart = Settings.Current.StartWithWindows;
      string autostartError = Autostart.LastError;
      bool warn = _app.HotkeyConflicts.Count > 0 || (autostart && autostartError != null);
      string text;
      if (_app.HotkeyConflicts.Count > 0) {
        text = "Another application already owns " +
               string.Join(", ", _app.HotkeyConflicts.ToArray()) +
               ". Those shortcuts will not fire until you pick a different combination.";
      } else if (autostart && autostartError != null) {
        text = "Windows would not let the app start itself at sign-in: " + autostartError +
               ". The shortcuts work now, but will not after a reboot.";
      } else {
        text = "Shortcuts are registered by the app itself - there is no separate listener to start. " +
               (autostart
                 ? "It starts in the tray when you sign in, so they work straight after a reboot."
                 : "Turn on \"Start with Windows\" to keep them after a reboot.");
      }

      Font font = Theme.Get(Dpi.S(11.5f), FontStyle.Regular);
      int dotSize = Dpi.S(7);
      // Measured at exactly the width it is drawn at; measuring against the
      // full box and then drawing into a narrower column silently clips the
      // last line.
      int textWidth = width - Dpi.S(24) - dotSize - Dpi.S(8);
      float textH = Theme.MeasureWrapped(g, text, font, textWidth);
      int boxH = (int)textH + Dpi.S(58);

      Rectangle r = Row(boxH);
      var box = new Rectangle(left, r.Y, width, boxH);

      var sw = new Rectangle(box.Right - Dpi.S(12) - Dpi.S(34),
                             box.Bottom - Dpi.S(12) - Dpi.S(19), Dpi.S(34), Dpi.S(19));
      AddPageHit("autostart", sw, true, null);

      if (!_measuring) {
        Theme.FillAndStroke(g, box, Dpi.S((float)Theme.RadiusSm), Theme.BgPanel, Theme.Border, 1f);

        // Green when every shortcut registered and will come back after a
        // reboot. Amber when another application owns one - those silently
        // never fire, which is otherwise impossible to diagnose - or when the
        // logon task could not be registered.
        using (var b = new SolidBrush(warn ? Theme.Warn : Theme.Ok))
          g.FillEllipse(b, box.X + Dpi.S(12), box.Y + Dpi.S(16), dotSize, dotSize);

        Theme.WrappedText(g, text, font, Theme.FgDim,
                          new RectangleF(box.X + Dpi.S(12) + dotSize + Dpi.S(8), box.Y + Dpi.S(11),
                                         textWidth, textH + Dpi.S(6)));

        Widgets.DrawSwitch(g, sw, autostart, Dpi);
        Font labelFont = Theme.Get(Dpi.S(12f), FontStyle.Regular);
        float lw = Theme.MeasureWidth(g, "Start with Windows", labelFont);
        Theme.Text(g, "Start with Windows", labelFont, Theme.Fg,
                   new RectangleF(sw.X - lw - Dpi.S(9), sw.Y - Dpi.S(2), lw + Dpi.S(4), Dpi.S(22)));
      }
      Gap(Dpi.S(6));
    }

    /// <summary>Hit regions inside the scrolling page live in page space until
    /// they are translated into the client area.</summary>
    void AddPageHit(string id, Rectangle pageRect, bool enabled, string tooltip) {
      if (_measuring) return;
      var client = new Rectangle(pageRect.X, pageRect.Y + BodyRect.Y - ScrollY, pageRect.Width, pageRect.Height);
      if (client.Bottom < BodyRect.Y || client.Top > BodyRect.Bottom) return;
      AddHit(id, client, enabled, tooltip);
    }

    // -------------------------------------------------------------- footer

    void DrawFooter(Graphics g) {
      int h = Dpi.S(FooterH);
      var r = new Rectangle(0, Height - h, Width, h);

      using (var b = new SolidBrush(Theme.BgPanel)) g.FillRectangle(b, r);
      using (var pen = new Pen(Theme.Border, 1f)) g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);

      int bh = Dpi.S(32);
      int y = r.Y + (h - bh) / 2;

      var reset = new Rectangle(Dpi.S(18), y, Dpi.S(140), bh);
      AddHit("reset", reset, true, null);
      Widgets.DrawButton(g, reset, "Reset to defaults", Widgets.ButtonStyle.Normal,
                          Hovered == "reset", PressedId == "reset", true, Dpi);

      var done = new Rectangle(Width - Dpi.S(18) - Dpi.S(84), y, Dpi.S(84), bh);
      AddHit("done", done, true, null);
      Widgets.DrawButton(g, done, "Done", Widgets.ButtonStyle.Primary,
                          Hovered == "done", PressedId == "done", true, Dpi);
    }

    // ------------------------------------------------------------ text boxes

    void PositionTextBoxes() {
      PlaceTextBox(_filename, _filenameBox);
      PlaceTextBox(_port, _portBox);
    }

    void PlaceTextBox(TextBox box, Rectangle pageRect) {
      if (box == null || pageRect.Width <= 0) return;
      int y = pageRect.Y + BodyRect.Y - ScrollY;
      bool visible = y + pageRect.Height > BodyRect.Y + Dpi.S(2) && y < BodyRect.Bottom - Dpi.S(2);

      box.Visible = visible;
      if (!visible) return;

      box.Font = Theme.Get(Dpi.S(13f), FontStyle.Regular);
      int inset = Dpi.S(9);
      int textH = box.PreferredHeight;
      box.SetBounds(pageRect.X + inset, y + (pageRect.Height - textH) / 2,
                    pageRect.Width - inset * 2, textH);
      box.BringToFront();
    }

    // ---------------------------------------------------------------- input

    string _openSelect;

    protected override void OnHit(Hit hit) {
      Settings s = Settings.Current;

      if (hit.Id == "close" || hit.Id == "done") { Close(); return; }

      if (hit.Id.StartsWith("switch:", StringComparison.Ordinal)) {
        switch (hit.Id.Substring(7)) {
          case "remuxOnStop": s.RemuxOnStop = !s.RemuxOnStop; break;
          case "captureCursor": s.CaptureCursor = !s.CaptureCursor; break;
          case "systemAudioEnabled": s.SystemAudioEnabled = !s.SystemAudioEnabled; break;
          case "micEnabled": s.MicEnabled = !s.MicEnabled; break;
          case "micAutoLevel": s.MicAutoLevel = !s.MicAutoLevel; break;
        }
        s.Save();
        Relayout();
        Invalidate();
        return;
      }

      if (hit.Id == "autostart") {
        s.StartWithWindows = !s.StartWithWindows;
        s.Save();
        // Registered on a worker; the status box repaints when it is done.
        _app.SyncAutostart();
        Invalidate();
        return;
      }

      if (hit.Id.StartsWith("select:", StringComparison.Ordinal)) { OpenSelect(hit); return; }

      if (hit.Id.StartsWith("hk:", StringComparison.Ordinal)) {
        _capturing = hit.Id.Substring(3);
        Focus();
        Invalidate();
        return;
      }

      switch (hit.Id) {
        case "chooseFolder": ChooseFolder(); break;
        case "openFolder": _app.OpenSaveFolder(); break;
        case "chooseFfmpeg": ChooseFfmpeg(); break;
        case "redetect": _app.RedetectEncoders(); _app.Toast("Re-detecting encoders…", ToastKind.Info); break;
        case "copyToken":
          try { Clipboard.SetText(_app.Bridge.Token); _app.Toast("Token copied", ToastKind.Success); }
          catch (Exception) { _app.Toast("Could not reach the clipboard", ToastKind.Warn); }
          break;
        case "extFolder": _app.OpenExtensionFolder(); break;
        case "reset":
          s.ResetToDefaults();
          _filename.Text = s.FilenamePattern;
          _port.Text = s.BridgePort.ToString(CultureInfo.InvariantCulture);
          _app.ReapplyHotkeys();
          _app.SyncAutostart();
          Relayout();
          Invalidate();
          break;
      }
    }

    protected override void OnHitDrag(Hit hit, Point p) {
      if (!hit.Id.StartsWith("slider:", StringComparison.Ordinal)) return;

      double t = (double)(p.X - hit.Bounds.X) / Math.Max(1, hit.Bounds.Width);
      if (t < 0) t = 0; else if (t > 1) t = 1;

      Settings s = Settings.Current;
      switch (hit.Id.Substring(7)) {
        case "quality": s.Quality = 14 + (int)Math.Round(t * 20); break;
        case "bitrateMbps": s.BitrateMbps = 2 + (int)Math.Round(t * 58); break;
        case "systemAudioGain": s.SystemAudioGain = Math.Round(t * 2 / 0.05) * 0.05; break;
        case "micGain": s.MicGain = Math.Round(t * 4 / 0.05) * 0.05; break;
        case "autoHide": s.AutoHideAfterStopSeconds = (int)(Math.Round(t * 120 / 5) * 5); break;
      }
      s.Save();
      Invalidate();
    }

    void OpenSelect(Hit hit) {
      string id = hit.Id.Substring(7);
      List<PopupList.Item> items = OptionsFor(id);
      if (items == null || items.Count == 0) return;

      _openSelect = id;
      Invalidate();

      var popup = new PopupList(items, CurrentValueFor(id), Dpi, hit.Bounds.Width);
      popup.Chosen += delegate (string value) { ApplySelect(id, value); };
      popup.FormClosed += delegate {
        _openSelect = null;
        Relayout();
        Invalidate();
      };
      popup.Open(PointToScreen(new Point(hit.Bounds.X, hit.Bounds.Bottom + Dpi.S(2))));
    }

    List<PopupList.Item> OptionsFor(string id) {
      var list = new List<PopupList.Item>();
      switch (id) {
        case "fps":
          foreach (int f in new[] { 24, 30, 48, 60, 120 })
            list.Add(new PopupList.Item(f.ToString(CultureInfo.InvariantCulture), f + " fps"));
          break;

        case "resolution":
          list.Add(new PopupList.Item("source", "Native (no scaling)"));
          list.Add(new PopupList.Item("2160", "2160p / 4K"));
          list.Add(new PopupList.Item("1440", "1440p"));
          list.Add(new PopupList.Item("1080", "1080p"));
          list.Add(new PopupList.Item("720", "720p"));
          list.Add(new PopupList.Item("480", "480p"));
          break;

        case "encoder": {
          EncoderSet set = _app.EncoderInfo;
          EncoderInfo best = set.Find(set.Best);
          list.Add(new PopupList.Item("auto", best != null ? "Automatic (" + best.Label + ")" : "Automatic"));
          foreach (EncoderInfo e in set.Available) list.Add(new PopupList.Item(e.Id, e.Label));
          break;
        }

        case "qualityMode":
          list.Add(new PopupList.Item("quality", "Constant quality"));
          list.Add(new PopupList.Item("bitrate", "Fixed bitrate"));
          break;

        case "micDeviceId":
          list.Add(new PopupList.Item("default", "System default"));
          foreach (AudioDeviceInfo d in _mics) list.Add(new PopupList.Item(d.Id, d.Name));
          break;
      }
      return list;
    }

    string CurrentValueFor(string id) {
      Settings s = Settings.Current;
      switch (id) {
        case "fps": return s.Fps.ToString(CultureInfo.InvariantCulture);
        case "resolution": return s.Resolution;
        case "encoder": return s.Encoder;
        case "qualityMode": return s.QualityMode;
        case "micDeviceId": return s.MicDeviceId;
      }
      return null;
    }

    void ApplySelect(string id, string value) {
      Settings s = Settings.Current;
      switch (id) {
        case "fps": { int v; if (int.TryParse(value, out v)) s.Fps = v; break; }
        case "resolution": s.Resolution = value; break;
        case "encoder": s.Encoder = value; break;
        case "qualityMode": s.QualityMode = value; break;
        case "micDeviceId": s.MicDeviceId = value; break;
      }
      s.Save();
      Relayout();
      Invalidate();
    }

    void ChooseFolder() {
      using (var dialog = new FolderBrowserDialog()) {
        dialog.Description = "Where should recordings be saved?";
        dialog.SelectedPath = Settings.Current.OutputDir;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Settings.Current.OutputDir = dialog.SelectedPath;
        Settings.Current.Save();
        Invalidate();
      }
    }

    void ChooseFfmpeg() {
      using (var dialog = new OpenFileDialog()) {
        dialog.Title = "Locate ffmpeg.exe";
        dialog.Filter = "ffmpeg|ffmpeg.exe|Programs (*.exe)|*.exe";
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Settings.Current.FfmpegPath = dialog.FileName;
        Settings.Current.Save();
        _app.RedetectEncoders();
        Invalidate();
      }
    }

    // ----------------------------------------------------------- key capture

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
      if (_capturing == null) return base.ProcessCmdKey(ref msg, keyData);

      // While a shortcut field is listening, every keystroke belongs to it.
      const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
      if (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN) return true;

      var e = new KeyEventArgs(keyData);

      if (e.KeyCode == Keys.Escape) { _capturing = null; Invalidate(); return true; }

      if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete) {
        Settings.Current.Hotkeys.Set(_capturing, "");
        Settings.Current.Save();
        _capturing = null;
        _app.ReapplyHotkeys();
        Invalidate();
        return true;
      }

      string accel = HotkeyManager.FromKeyEvent(e);
      if (accel == null) return true;                 // a modifier on its own

      Settings.Current.Hotkeys.Set(_capturing, accel);
      Settings.Current.Save();
      _capturing = null;
      _app.ReapplyHotkeys();
      Invalidate();
      return true;
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      // Escape must not close the window while a shortcut field has focus.
      if (e.KeyCode == Keys.Escape && _capturing == null) Close();
    }

    // -------------------------------------------------------------- labels

    static string ResolutionLabel(string value) {
      switch (value) {
        case "source": return "Native (no scaling)";
        case "2160": return "2160p / 4K";
        case "1440": return "1440p";
        case "1080": return "1080p";
        case "720": return "720p";
        case "480": return "480p";
      }
      return value;
    }

    string EncoderLabel(string value) {
      EncoderSet set = _app.EncoderInfo;
      if (value == "auto" || string.IsNullOrEmpty(value)) {
        EncoderInfo best = set.Find(set.Best);
        return best != null ? "Automatic (" + best.Label + ")" : "Automatic";
      }
      EncoderInfo chosen = set.Find(value);
      return chosen != null ? chosen.Label : value;
    }

    string EncoderHint() {
      EncoderSet set = _app.EncoderInfo;
      if (set.Available.Count == 0) return "No encoders detected - check the ffmpeg path below.";

      EncoderInfo picked = set.Find(set.Pick(Settings.Current.Encoder));
      bool gpuDirect = picked != null && picked.AcceptsD3D11 && set.HasDdagrab;
      bool scaling = Settings.Current.Resolution != "source";

      if (gpuDirect && !scaling)
        return "Frames go straight from the screen to the encoder without ever touching the CPU. " +
               "This is by far the cheapest path - choosing any resolution other than Native gives it up.";
      if (gpuDirect && scaling)
        return "Scaling forces every frame through the CPU. Set Resolution to Native to keep the whole " +
               "pipeline on the GPU, which costs a fraction of the CPU time.";

      bool anyHardware = false;
      foreach (EncoderInfo e in set.Available) if (e.Hardware) { anyHardware = true; break; }
      return anyHardware
        ? "Hardware encoding keeps the CPU free, which is what makes long game recordings cheap."
        : "Only software encoding is available, so expect noticeable CPU use.";
    }

    string FfmpegHint() {
      EncoderSet set = _app.EncoderInfo;
      if (set.Ffmpeg == null) return "Not found. - Screen and window recording need it.";
      return set.HasDdagrab
        ? "Desktop Duplication available - GPU screen capture, ideal for games."
        : "No ddagrab filter - falling back to GDI capture, which uses more CPU.";
    }

    bool FfmpegHintGood() {
      EncoderSet set = _app.EncoderInfo;
      return set.Ffmpeg != null && set.HasDdagrab;
    }

    string MicLabel(string id) {
      if (string.IsNullOrEmpty(id) || id == "default") return "System default";
      foreach (AudioDeviceInfo d in _mics) if (d.Id == id) return d.Name;
      return "System default";
    }

    static string Percent(double gain) {
      return (int)Math.Round(gain * 100) + "%";
    }
  }
}
