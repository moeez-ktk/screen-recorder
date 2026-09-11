using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LightRecorder.Ui {

  /// <summary>
  /// The design tokens from theme.css, ported one for one so the app looks
  /// like it always did. Everything is drawn rather than themed, which is what
  /// lets a dark rounded pill with a real drop shadow exist without a browser
  /// engine underneath it.
  /// </summary>
  internal static class Theme {

    public static readonly Color Bg = Color.FromArgb(0x14, 0x16, 0x1a);
    public static readonly Color BgPanel = Color.FromArgb(0x1b, 0x1e, 0x24);
    public static readonly Color BgRaise = Color.FromArgb(0x23, 0x27, 0x2f);
    public static readonly Color BgHover = Color.FromArgb(0x2b, 0x31, 0x3b);

    public static readonly Color Fg = Color.FromArgb(0xe8, 0xea, 0xed);
    public static readonly Color FgDim = Color.FromArgb(0x9a, 0xa0, 0xa6);
    public static readonly Color FgFaint = Color.FromArgb(0x6b, 0x72, 0x80);

    public static readonly Color Border = Color.FromArgb(26, 255, 255, 255);        // rgba(255,255,255,.10)
    public static readonly Color BorderStrong = Color.FromArgb(46, 255, 255, 255);  // rgba(255,255,255,.18)

    public static readonly Color Accent = Color.FromArgb(0x4a, 0x9e, 0xff);
    public static readonly Color AccentDim = Color.FromArgb(38, 0x4a, 0x9e, 0xff);
    public static readonly Color Rec = Color.FromArgb(0xff, 0x4d, 0x4f);
    public static readonly Color Ok = Color.FromArgb(0x34, 0xc7, 0x59);
    public static readonly Color Warn = Color.FromArgb(0xff, 0xb0, 0x20);

    /// <summary>rgba(20,22,26,0.94) - the translucent pill and mixer fill.</summary>
    public static readonly Color Glass = Color.FromArgb(240, 0x14, 0x16, 0x1a);

    public const int Radius = 10;
    public const int RadiusSm = 7;

    // ---------------------------------------------------------------- fonts

    const string PreferredFamily = "Segoe UI Variable Text";
    const string FallbackFamily = "Segoe UI";

    static string _family;
    public static string Family {
      get {
        if (_family == null) {
          _family = FallbackFamily;
          try {
            using (var probe = new FontFamily(PreferredFamily)) _family = PreferredFamily;
          } catch (Exception) {
            // Windows 10 does not ship the Variable family; Segoe UI is the
            // same design at these sizes.
          }
        }
        return _family;
      }
    }

    static string _mono;
    public static string MonoFamily {
      get {
        if (_mono == null) {
          _mono = "Consolas";
          try {
            using (var probe = new FontFamily("Cascadia Mono")) _mono = "Cascadia Mono";
          } catch (Exception) { }
        }
        return _mono;
      }
    }

    /// <summary>
    /// Fonts are cached because creating one is a GDI+ round trip and the
    /// windows redraw on every hover. Keyed on the pixel size actually used,
    /// so a per-monitor DPI change just adds an entry.
    /// </summary>
    static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
    static readonly object FontGate = new object();

    public static Font Get(float px, FontStyle style) {
      return Get(Family, px, style);
    }

    public static Font Mono(float px, FontStyle style) {
      return Get(MonoFamily, px, style);
    }

    public static Font Get(string family, float px, FontStyle style) {
      if (px < 1) px = 1;
      string key = family + "|" + px.ToString("0.##") + "|" + (int)style;
      lock (FontGate) {
        Font f;
        if (FontCache.TryGetValue(key, out f)) return f;
        try {
          f = new Font(family, px, style, GraphicsUnit.Pixel);
        } catch (Exception) {
          f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel);
        }
        // Bounded against a pathological DPI-change loop; the working set is a
        // dozen fonts in practice.
        if (FontCache.Count > 96) { foreach (Font old in FontCache.Values) old.Dispose(); FontCache.Clear(); }
        FontCache[key] = f;
        return f;
      }
    }

    // ------------------------------------------------------------- drawing

    public static void Prepare(Graphics g) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBilinear;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      // ClearType cannot work on a per-pixel-alpha surface - it would smear
      // colour fringes over transparency - so grayscale antialiasing it is.
      g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius) {
      var path = new GraphicsPath();
      float d = radius * 2;
      if (d <= 0 || r.Width <= d || r.Height <= d) {
        // Degenerate: a pill narrower than its corners, or a plain rectangle.
        if (d > 0 && r.Height > 0 && r.Width > 0) {
          d = Math.Min(Math.Min(d, r.Width), r.Height);
          if (d <= 0) { path.AddRectangle(r); return path; }
        } else {
          path.AddRectangle(r);
          return path;
        }
      }
      path.AddArc(r.X, r.Y, d, d, 180, 90);
      path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
      path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      path.CloseFigure();
      return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill) {
      using (GraphicsPath p = RoundedRect(r, radius))
      using (var b = new SolidBrush(fill)) g.FillPath(b, p);
    }

    public static void DrawRounded(Graphics g, RectangleF r, float radius, Color stroke, float width) {
      using (GraphicsPath p = RoundedRect(r, radius))
      using (var pen = new Pen(stroke, width)) g.DrawPath(pen, p);
    }

    public static void FillAndStroke(Graphics g, RectangleF r, float radius, Color fill, Color stroke, float width) {
      using (GraphicsPath p = RoundedRect(r, radius)) {
        using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        using (var pen = new Pen(stroke, width)) g.DrawPath(pen, p);
      }
    }

    // ---------------------------------------------------------------- text

    static readonly StringFormat Left = MakeFormat(StringAlignment.Near, StringAlignment.Center);
    static readonly StringFormat Centre = MakeFormat(StringAlignment.Center, StringAlignment.Center);
    static readonly StringFormat LeftTop = MakeFormat(StringAlignment.Near, StringAlignment.Near);

    static StringFormat MakeFormat(StringAlignment h, StringAlignment v) {
      var f = new StringFormat(StringFormat.GenericTypographic);
      f.Alignment = h;
      f.LineAlignment = v;
      f.FormatFlags |= StringFormatFlags.NoWrap;
      f.Trimming = StringTrimming.EllipsisCharacter;
      return f;
    }

    public static void Text(Graphics g, string s, Font font, Color colour, RectangleF box) {
      Text(g, s, font, colour, box, Left);
    }

    public static void TextCentred(Graphics g, string s, Font font, Color colour, RectangleF box) {
      Text(g, s, font, colour, box, Centre);
    }

    public static void Text(Graphics g, string s, Font font, Color colour, RectangleF box, StringFormat format) {
      if (string.IsNullOrEmpty(s)) return;
      using (var b = new SolidBrush(colour)) g.DrawString(s, font, b, box, format);
    }

    /// <summary>Word-wrapped body text, used for the hints under settings.</summary>
    public static float WrappedText(Graphics g, string s, Font font, Color colour, RectangleF box) {
      if (string.IsNullOrEmpty(s)) return 0;
      using (var f = new StringFormat(StringFormat.GenericTypographic)) {
        f.Alignment = StringAlignment.Near;
        f.LineAlignment = StringAlignment.Near;
        f.FormatFlags &= ~StringFormatFlags.NoWrap;
        using (var b = new SolidBrush(colour)) g.DrawString(s, font, b, box, f);
        SizeF size = g.MeasureString(s, font, (int)box.Width, f);
        return size.Height;
      }
    }

    public static float MeasureWrapped(Graphics g, string s, Font font, float width) {
      if (string.IsNullOrEmpty(s)) return 0;
      using (var f = new StringFormat(StringFormat.GenericTypographic)) {
        f.FormatFlags &= ~StringFormatFlags.NoWrap;
        return g.MeasureString(s, font, (int)width, f).Height;
      }
    }

    public static float MeasureWidth(Graphics g, string s, Font font) {
      if (string.IsNullOrEmpty(s)) return 0;
      // A hair of slack. GenericTypographic measures tightly, and laying a
      // string out in a box of exactly its measured width rounds down often
      // enough to trigger the ellipsis on text that fits perfectly well.
      return g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width + 2f;
    }

    public static StringFormat LeftFormat { get { return Left; } }
    public static StringFormat CentreFormat { get { return Centre; } }
    public static StringFormat TopLeftFormat { get { return LeftTop; } }

    // -------------------------------------------------------------- shadow

    static readonly Dictionary<string, Bitmap> ShadowCache = new Dictionary<string, Bitmap>();

    /// <summary>
    /// A real blurred drop shadow, matching box-shadow: 0 Ypx Bpx rgba(0,0,0,a).
    ///
    /// GDI+ has no blur, so the shape is rasterised into an alpha mask, box
    /// blurred three times (which converges on a Gaussian), and cached by
    /// geometry. The windows only repaint on state changes, so this is paid
    /// once per size rather than per frame.
    /// </summary>
    // The overlay asks for the same shadow several times a second while
    // recording. Remembering the last answer skips even building the cache key,
    // which is otherwise a string concatenation per frame for a dictionary that
    // is going to return the same bitmap it returned last time.
    static int _lastW, _lastH, _lastBlur, _lastAlpha;
    static float _lastRadius;
    static Bitmap _lastShadow;

    public static Bitmap Shadow(int width, int height, float radius, int blur, int alpha) {
      if (_lastShadow != null && width == _lastW && height == _lastH &&
          radius == _lastRadius && blur == _lastBlur && alpha == _lastAlpha)
        return _lastShadow;

      string key = width + "x" + height + "r" + radius + "b" + blur + "a" + alpha;
      lock (ShadowCache) {
        Bitmap cached;
        if (ShadowCache.TryGetValue(key, out cached)) {
          _lastW = width; _lastH = height; _lastRadius = radius;
          _lastBlur = blur; _lastAlpha = alpha; _lastShadow = cached;
          return cached;
        }
      }

      int pad = blur * 2;
      int w = width + pad * 2, h = height + pad * 2;
      if (w <= 0 || h <= 0 || w > 4000 || h > 4000) return null;

      var mask = new byte[w * h];
      using (var shape = new Bitmap(w, h, PixelFormat.Format32bppArgb))
      using (Graphics g = Graphics.FromImage(shape)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath p = RoundedRect(new RectangleF(pad, pad, width, height), radius))
        using (var b = new SolidBrush(Color.Black)) g.FillPath(b, p);

        BitmapData data = shape.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
          unsafe {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < h; y++) {
              byte* row = scan + y * data.Stride;
              for (int x = 0; x < w; x++) mask[y * w + x] = row[x * 4 + 3];
            }
          }
        } finally {
          shape.UnlockBits(data);
        }
      }

      BoxBlur(mask, w, h, Math.Max(1, blur / 2));
      BoxBlur(mask, w, h, Math.Max(1, blur / 2));
      BoxBlur(mask, w, h, Math.Max(1, blur / 3));

      var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      BitmapData outData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      try {
        unsafe {
          byte* scan = (byte*)outData.Scan0;
          for (int y = 0; y < h; y++) {
            byte* row = scan + y * outData.Stride;
            for (int x = 0; x < w; x++) {
              int a = mask[y * w + x] * alpha / 255;
              // Premultiplied against black, so RGB stays zero.
              row[x * 4 + 0] = 0;
              row[x * 4 + 1] = 0;
              row[x * 4 + 2] = 0;
              row[x * 4 + 3] = (byte)a;
            }
          }
        }
      } finally {
        result.UnlockBits(outData);
      }

      lock (ShadowCache) {
        if (ShadowCache.Count > 24) {
          foreach (Bitmap old in ShadowCache.Values) old.Dispose();
          ShadowCache.Clear();
          _lastShadow = null;
        }
        ShadowCache[key] = result;
      }
      _lastW = width; _lastH = height; _lastRadius = radius;
      _lastBlur = blur; _lastAlpha = alpha; _lastShadow = result;
      return result;
    }

    /// <summary>Separable box blur over an 8-bit alpha plane.</summary>
    static void BoxBlur(byte[] a, int w, int h, int r) {
      if (r < 1) return;
      var tmp = new byte[a.Length];
      int span = r * 2 + 1;

      for (int y = 0; y < h; y++) {
        int row = y * w;
        int sum = 0;
        for (int x = -r; x <= r; x++) sum += a[row + Clamp(x, 0, w - 1)];
        for (int x = 0; x < w; x++) {
          tmp[row + x] = (byte)(sum / span);
          sum -= a[row + Clamp(x - r, 0, w - 1)];
          sum += a[row + Clamp(x + r + 1, 0, w - 1)];
        }
      }

      for (int x = 0; x < w; x++) {
        int sum = 0;
        for (int y = -r; y <= r; y++) sum += tmp[Clamp(y, 0, h - 1) * w + x];
        for (int y = 0; y < h; y++) {
          a[y * w + x] = (byte)(sum / span);
          sum -= tmp[Clamp(y - r, 0, h - 1) * w + x];
          sum += tmp[Clamp(y + r + 1, 0, h - 1) * w + x];
        }
      }
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    /// <summary>Blend towards another colour, for hover states.</summary>
    public static Color Mix(Color a, Color b, float t) {
      return Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t),
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color c, int alpha) {
      return Color.FromArgb(alpha, c.R, c.G, c.B);
    }
  }

  /// <summary>
  /// Per-monitor DPI scaling. Every layout number in the UI is written at the
  /// CSS size and passed through here, so a 150% display gets a pill that is
  /// actually bigger rather than a blurry stretched bitmap.
  /// </summary>
  internal class Dpi {
    public int Value = 96;
    public float Scale { get { return Value / 96f; } }

    public int S(int px) { return (int)Math.Round(px * Scale); }
    public float S(float px) { return px * Scale; }

    public void FromWindow(IntPtr hWnd) { Value = Native.DpiForWindow(hWnd); }
    public void FromPoint(Point p) { Value = Native.DpiForPoint(p); }
  }
}
