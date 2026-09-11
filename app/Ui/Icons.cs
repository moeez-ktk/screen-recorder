using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace LightRecorder.Ui {

  /// <summary>
  /// The overlay's icons.
  ///
  /// Rather than redraw them by hand and hope they came out the same, the SVG
  /// path data is copied verbatim out of the old overlay.html and rendered
  /// through a small path parser. The icons are therefore identical to the ones
  /// the app shipped with, they scale cleanly to any DPI, and they cost no
  /// image assets at all.
  /// </summary>
  internal static class Icons {

    public const string Record = "record";
    public const string Stop = "stop";
    public const string Pause = "pause";
    public const string Monitor = "monitor";
    public const string Mic = "mic";
    public const string Speaker = "speaker";
    public const string Mixer = "mixer";
    public const string Settings = "settings";
    public const string Chevron = "chevron";
    public const string Close = "close";
    public const string Refresh = "refresh";
    public const string AppBox = "appbox";
    public const string Folder = "folder";
    public const string Check = "check";
    public const string ChevronDown = "chevrondown";

    class Shape {
      public string Data;                  // svg path
      public RectangleF Rect;              // rounded rect, when Data is null
      public float Rx;
      public bool IsRect;
      public bool Fill;
      public string Tag;                   // "slash" and "wave" toggle with mute

      /// <summary>
      /// Built once, then reused forever.
      ///
      /// The geometry is fixed in the icon's own 24x24 space and every size and
      /// position comes from the Graphics transform, so there is nothing
      /// per-draw about it. Rebuilding it each time meant re-parsing a path
      /// string and allocating a GDI+ path object for every icon on every
      /// frame - about a hundred and sixty a second while the overlay's
      /// recording animation runs.
      /// </summary>
      public GraphicsPath Cached;
    }

    class Def {
      public List<Shape> Shapes = new List<Shape>();
      public float StrokeWidth = 1.8f;
    }

    static readonly Dictionary<string, Def> Library = Build();

    static Def Make(float stroke, params Shape[] shapes) {
      var d = new Def();
      d.StrokeWidth = stroke;
      d.Shapes.AddRange(shapes);
      return d;
    }

    static Shape P(string data) { var s = new Shape(); s.Data = data; return s; }
    static Shape PF(string data) { var s = new Shape(); s.Data = data; s.Fill = true; return s; }
    static Shape Tagged(string data, string tag) { var s = new Shape(); s.Data = data; s.Tag = tag; return s; }
    static Shape R(float x, float y, float w, float h, float rx) {
      var s = new Shape(); s.IsRect = true; s.Rect = new RectangleF(x, y, w, h); s.Rx = rx; return s;
    }
    static Shape RF(float x, float y, float w, float h, float rx) {
      Shape s = R(x, y, w, h, rx); s.Fill = true; return s;
    }

    static Dictionary<string, Def> Build() {
      var m = new Dictionary<string, Def>(StringComparer.Ordinal);

      // <circle cx="12" cy="12" r="8"/> filled
      m[Record] = Make(0, PF("M4 12a8 8 0 1 0 16 0a8 8 0 1 0 -16 0Z"));

      // <rect x="6" y="6" width="12" height="12" rx="2"/> filled
      m[Stop] = Make(0, RF(6, 6, 12, 12, 2));

      // Two bars on the same 12px grid as stop, so the pair reads as a set.
      m[Pause] = Make(0, RF(6, 5, 4, 14, 1.5f), RF(14, 5, 4, 14, 1.5f));

      m[Monitor] = Make(1.8f, R(2, 4, 20, 13, 2), P("M8 21h8M12 17v4"));

      m[Mic] = Make(1.8f,
        R(9, 2, 6, 12, 3),
        P("M5 11a7 7 0 0 0 14 0M12 18v4"),
        Tagged("M3 3L21 21", "slash"));

      m[Speaker] = Make(1.8f,
        P("M4 9v6h4l5 4V5L8 9H4z"),
        Tagged("M17 8.5a5 5 0 0 1 0 7", "wave"),
        Tagged("M3 3L21 21", "slash"));

      m[Mixer] = Make(1.8f,
        P("M5 21V14M5 10V3M12 21v-9M12 8V3M19 21v-5M19 12V3"),
        P("M2 14h6M9 8h6M16 16h6"));

      m[Settings] = Make(1.8f,
        P("M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0Z"),
        P("M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 " +
          "1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 " +
          "1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 " +
          "1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"));

      m[Chevron] = Make(2f, P("M15 6l-6 6 6 6"));
      m[ChevronDown] = Make(2f, P("M6 9l6 6 6-6"));
      m[Close] = Make(2f, P("M6 6l12 12M18 6L6 18"));
      m[Refresh] = Make(1.8f, P("M21 12a9 9 0 1 1-2.6-6.4"), P("M21 3v6h-6"));
      m[AppBox] = Make(1.6f, R(3, 3, 18, 18, 3), P("M9 9h6v6H9z"));
      m[Folder] = Make(1.8f, P("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"));
      m[Check] = Make(2.2f, P("M20 6L9 17l-5-5"));

      return m;
    }

    // ------------------------------------------------------------- drawing

    /// <summary>
    /// Render a 24x24 icon into <paramref name="box"/>.
    /// <paramref name="muted"/> shows the strike-through and hides the speaker
    /// wave, exactly as the CSS did - it keeps the two audio buttons readable
    /// at a glance without needing a second icon.
    /// </summary>
    public static void Draw(Graphics g, string name, RectangleF box, Color colour, bool muted) {
      Def def;
      if (!Library.TryGetValue(name, out def)) return;

      float scale = Math.Min(box.Width, box.Height) / 24f;
      if (scale <= 0) return;

      GraphicsState saved = g.Save();
      try {
        g.TranslateTransform(box.X + (box.Width - 24 * scale) / 2f,
                             box.Y + (box.Height - 24 * scale) / 2f);
        g.ScaleTransform(scale, scale);

        using (var pen = new Pen(colour, def.StrokeWidth))
        using (var brush = new SolidBrush(colour)) {
          pen.StartCap = LineCap.Round;
          pen.EndCap = LineCap.Round;
          pen.LineJoin = LineJoin.Round;

          foreach (Shape s in def.Shapes) {
            if (s.Tag == "slash" && !muted) continue;
            if (s.Tag == "wave" && muted) continue;

            GraphicsPath path = ShapePath(s);
            if (path == null) continue;
            if (s.Fill) g.FillPath(brush, path);
            else g.DrawPath(pen, path);
          }
        }
      } finally {
        g.Restore(saved);
      }
    }

    static readonly object PathGate = new object();

    static GraphicsPath ShapePath(Shape s) {
      if (s.Cached != null) return s.Cached;
      lock (PathGate) {
        if (s.Cached == null)
          s.Cached = s.IsRect ? Theme.RoundedRect(s.Rect, s.Rx) : Parse(s.Data);
      }
      return s.Cached;
    }

    // ------------------------------------------------------- path parsing

    /// <summary>
    /// An SVG path parser covering the commands these icons use: M/L/H/V/C/S/
    /// Q/T/A/Z in both absolute and relative form, with implicit repeats and
    /// the compact flag syntax arcs use ("1 1-2.6" is three values, not two).
    /// </summary>
    public static GraphicsPath Parse(string d) {
      if (string.IsNullOrEmpty(d)) return null;

      var path = new GraphicsPath();
      var cursor = new PointF(0, 0);
      var start = new PointF(0, 0);
      PointF lastControl = new PointF(0, 0);
      char lastCommand = ' ';
      bool open = false;

      int i = 0;
      char command = ' ';

      while (i < d.Length) {
        SkipSeparators(d, ref i);
        if (i >= d.Length) break;

        char c = d[i];
        if (char.IsLetter(c)) { command = c; i++; }
        else if (command == ' ') break;
        else if (command == 'M') command = 'L';        // implicit lineto after moveto
        else if (command == 'm') command = 'l';

        bool relative = char.IsLower(command);
        char op = char.ToUpperInvariant(command);

        switch (op) {
          case 'M': {
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { x += cursor.X; y += cursor.Y; }
            if (open) { /* a new subpath simply starts */ }
            path.StartFigure();
            cursor = new PointF(x, y);
            start = cursor;
            open = true;
            break;
          }

          case 'L': {
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { x += cursor.X; y += cursor.Y; }
            path.AddLine(cursor, new PointF(x, y));
            cursor = new PointF(x, y);
            break;
          }

          case 'H': {
            float x = Number(d, ref i);
            if (relative) x += cursor.X;
            path.AddLine(cursor, new PointF(x, cursor.Y));
            cursor = new PointF(x, cursor.Y);
            break;
          }

          case 'V': {
            float y = Number(d, ref i);
            if (relative) y += cursor.Y;
            path.AddLine(cursor, new PointF(cursor.X, y));
            cursor = new PointF(cursor.X, y);
            break;
          }

          case 'C': {
            float x1 = Number(d, ref i), y1 = Number(d, ref i);
            float x2 = Number(d, ref i), y2 = Number(d, ref i);
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) {
              x1 += cursor.X; y1 += cursor.Y;
              x2 += cursor.X; y2 += cursor.Y;
              x += cursor.X; y += cursor.Y;
            }
            path.AddBezier(cursor, new PointF(x1, y1), new PointF(x2, y2), new PointF(x, y));
            lastControl = new PointF(x2, y2);
            cursor = new PointF(x, y);
            break;
          }

          case 'S': {
            float x2 = Number(d, ref i), y2 = Number(d, ref i);
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { x2 += cursor.X; y2 += cursor.Y; x += cursor.X; y += cursor.Y; }
            PointF c1 = (lastCommand == 'C' || lastCommand == 'S')
              ? new PointF(2 * cursor.X - lastControl.X, 2 * cursor.Y - lastControl.Y)
              : cursor;
            path.AddBezier(cursor, c1, new PointF(x2, y2), new PointF(x, y));
            lastControl = new PointF(x2, y2);
            cursor = new PointF(x, y);
            break;
          }

          case 'Q': {
            float qx = Number(d, ref i), qy = Number(d, ref i);
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { qx += cursor.X; qy += cursor.Y; x += cursor.X; y += cursor.Y; }
            AddQuadratic(path, cursor, new PointF(qx, qy), new PointF(x, y));
            lastControl = new PointF(qx, qy);
            cursor = new PointF(x, y);
            break;
          }

          case 'T': {
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { x += cursor.X; y += cursor.Y; }
            PointF q = (lastCommand == 'Q' || lastCommand == 'T')
              ? new PointF(2 * cursor.X - lastControl.X, 2 * cursor.Y - lastControl.Y)
              : cursor;
            AddQuadratic(path, cursor, q, new PointF(x, y));
            lastControl = q;
            cursor = new PointF(x, y);
            break;
          }

          case 'A': {
            float rx = Number(d, ref i), ry = Number(d, ref i);
            float rotation = Number(d, ref i);
            // The flags are single characters and may be run together with the
            // number that follows them.
            bool largeArc = Flag(d, ref i);
            bool sweep = Flag(d, ref i);
            float x = Number(d, ref i), y = Number(d, ref i);
            if (relative) { x += cursor.X; y += cursor.Y; }
            AddArc(path, cursor, new PointF(x, y), rx, ry, rotation, largeArc, sweep);
            cursor = new PointF(x, y);
            break;
          }

          case 'Z':
            path.CloseFigure();
            cursor = start;
            open = false;
            break;

          default:
            return path;      // an unknown command means the rest is unreadable
        }

        lastCommand = op;
      }

      return path;
    }

    static void AddQuadratic(GraphicsPath path, PointF p0, PointF q, PointF p1) {
      // GDI+ has no quadratic segment; the exact cubic equivalent is standard.
      var c1 = new PointF(p0.X + 2f / 3f * (q.X - p0.X), p0.Y + 2f / 3f * (q.Y - p0.Y));
      var c2 = new PointF(p1.X + 2f / 3f * (q.X - p1.X), p1.Y + 2f / 3f * (q.Y - p1.Y));
      path.AddBezier(p0, c1, c2, p1);
    }

    /// <summary>
    /// SVG's endpoint arc parameterisation converted to the centre form GDI+
    /// wants. Straight out of the specification's implementation notes.
    /// </summary>
    static void AddArc(GraphicsPath path, PointF from, PointF to,
                       float rx, float ry, float rotationDeg, bool largeArc, bool sweep) {
      if (rx == 0 || ry == 0 || (from.X == to.X && from.Y == to.Y)) {
        path.AddLine(from, to);
        return;
      }

      rx = Math.Abs(rx);
      ry = Math.Abs(ry);
      double phi = rotationDeg * Math.PI / 180.0;
      double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

      double dx2 = (from.X - to.X) / 2.0, dy2 = (from.Y - to.Y) / 2.0;
      double x1p = cosPhi * dx2 + sinPhi * dy2;
      double y1p = -sinPhi * dx2 + cosPhi * dy2;

      // Scale the radii up if they are too small to span the chord.
      double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
      if (lambda > 1) {
        double k = Math.Sqrt(lambda);
        rx = (float)(rx * k);
        ry = (float)(ry * k);
      }

      double sign = (largeArc != sweep) ? 1 : -1;
      double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
      double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
      double coef = den == 0 ? 0 : sign * Math.Sqrt(Math.Max(0, num / den));

      double cxp = coef * rx * y1p / ry;
      double cyp = -coef * ry * x1p / rx;

      double cx = cosPhi * cxp - sinPhi * cyp + (from.X + to.X) / 2.0;
      double cy = sinPhi * cxp + cosPhi * cyp + (from.Y + to.Y) / 2.0;

      double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
      double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;

      double startAngle = Angle(1, 0, ux, uy);
      double delta = Angle(ux, uy, vx, vy);

      if (!sweep && delta > 0) delta -= 360;
      else if (sweep && delta < 0) delta += 360;

      // GDI+ cannot rotate an arc, but none of these icons need it: every arc
      // in them has a zero x-axis rotation.
      path.AddArc((float)(cx - rx), (float)(cy - ry), rx * 2, ry * 2,
                  (float)startAngle, (float)delta);
    }

    static double Angle(double ux, double uy, double vx, double vy) {
      double dot = ux * vx + uy * vy;
      double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
      if (len == 0) return 0;
      double cos = dot / len;
      if (cos > 1) cos = 1; else if (cos < -1) cos = -1;
      double angle = Math.Acos(cos) * 180.0 / Math.PI;
      if (ux * vy - uy * vx < 0) angle = -angle;
      return angle;
    }

    // ------------------------------------------------------------ scanning

    static void SkipSeparators(string s, ref int i) {
      while (i < s.Length) {
        char c = s[i];
        if (c == ' ' || c == ',' || c == '\t' || c == '\n' || c == '\r') i++;
        else break;
      }
    }

    static float Number(string s, ref int i) {
      SkipSeparators(s, ref i);
      int start = i;
      if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
      while (i < s.Length && char.IsDigit(s[i])) i++;
      if (i < s.Length && s[i] == '.') {
        i++;
        while (i < s.Length && char.IsDigit(s[i])) i++;
      }
      if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) {
        i++;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && char.IsDigit(s[i])) i++;
      }
      if (i == start) { i++; return 0; }

      float value;
      float.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
      return value;
    }

    /// <summary>Arc flags are exactly one character wide, which is what lets
    /// "1 1-2.6-6.4" parse correctly.</summary>
    static bool Flag(string s, ref int i) {
      SkipSeparators(s, ref i);
      if (i >= s.Length) return false;
      char c = s[i++];
      return c == '1';
    }
  }
}
