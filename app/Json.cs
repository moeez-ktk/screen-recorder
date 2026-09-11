using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LightRecorder {

  /// <summary>
  /// A whole JSON implementation in one file, because the alternative is
  /// dragging in System.Web.Extensions (a megabyte of assembly) or a NuGet
  /// package (a package manager) to move a few hundred bytes of settings and
  /// bridge traffic around.
  ///
  /// Objects come back as Dictionary&lt;string,object&gt;, arrays as
  /// List&lt;object&gt;, numbers as double, everything else as the obvious
  /// CLR type. That is enough for both the settings file and the extension
  /// protocol, and it never allocates a reflection cache.
  /// </summary>
  internal static class Json {

    // ------------------------------------------------------------- parsing

    public static object Parse(string text) {
      if (string.IsNullOrEmpty(text)) return null;
      int i = 0;
      object v = ParseValue(text, ref i);
      return v;
    }

    /// <summary>Parse, returning an empty object rather than throwing.</summary>
    public static Dictionary<string, object> ParseObject(string text) {
      try {
        Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
        if (d != null) return d;
      } catch (Exception) { }
      return new Dictionary<string, object>();
    }

    static object ParseValue(string s, ref int i) {
      SkipWs(s, ref i);
      if (i >= s.Length) throw new FormatException("unexpected end of JSON");

      char c = s[i];
      switch (c) {
        case '{': return ParseObj(s, ref i);
        case '[': return ParseArr(s, ref i);
        case '"': return ParseString(s, ref i);
        case 't': Expect(s, ref i, "true"); return true;
        case 'f': Expect(s, ref i, "false"); return false;
        case 'n': Expect(s, ref i, "null"); return null;
        default: return ParseNumber(s, ref i);
      }
    }

    static Dictionary<string, object> ParseObj(string s, ref int i) {
      var map = new Dictionary<string, object>();
      i++;                                    // '{'
      SkipWs(s, ref i);
      if (i < s.Length && s[i] == '}') { i++; return map; }

      while (true) {
        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key");
        string key = ParseString(s, ref i);
        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
        i++;
        map[key] = ParseValue(s, ref i);
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("unterminated object");
        if (s[i] == ',') { i++; continue; }
        if (s[i] == '}') { i++; return map; }
        throw new FormatException("expected ',' or '}'");
      }
    }

    static List<object> ParseArr(string s, ref int i) {
      var list = new List<object>();
      i++;                                    // '['
      SkipWs(s, ref i);
      if (i < s.Length && s[i] == ']') { i++; return list; }

      while (true) {
        list.Add(ParseValue(s, ref i));
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("unterminated array");
        if (s[i] == ',') { i++; continue; }
        if (s[i] == ']') { i++; return list; }
        throw new FormatException("expected ',' or ']'");
      }
    }

    static string ParseString(string s, ref int i) {
      i++;                                    // opening quote
      var sb = new StringBuilder();
      while (i < s.Length) {
        char c = s[i++];
        if (c == '"') return sb.ToString();
        if (c != '\\') { sb.Append(c); continue; }

        if (i >= s.Length) break;
        char e = s[i++];
        switch (e) {
          case '"': sb.Append('"'); break;
          case '\\': sb.Append('\\'); break;
          case '/': sb.Append('/'); break;
          case 'b': sb.Append('\b'); break;
          case 'f': sb.Append('\f'); break;
          case 'n': sb.Append('\n'); break;
          case 'r': sb.Append('\r'); break;
          case 't': sb.Append('\t'); break;
          case 'u':
            if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
            sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture));
            i += 4;
            break;
          default: throw new FormatException("bad escape \\" + e);
        }
      }
      throw new FormatException("unterminated string");
    }

    static object ParseNumber(string s, ref int i) {
      int start = i;
      if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
      while (i < s.Length) {
        char c = s[i];
        if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
        else break;
      }
      if (i == start) throw new FormatException("expected a number at " + start);
      return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
    }

    static void Expect(string s, ref int i, string word) {
      if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
        throw new FormatException("expected " + word);
      i += word.Length;
    }

    static void SkipWs(string s, ref int i) {
      while (i < s.Length) {
        char c = s[i];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
        else break;
      }
    }

    // ------------------------------------------------------------ writing

    public static string Write(object value) {
      var sb = new StringBuilder(256);
      WriteValue(sb, value, -1, 0);
      return sb.ToString();
    }

    /// <summary>Indented output, for files a human might open.</summary>
    public static string WritePretty(object value) {
      var sb = new StringBuilder(1024);
      WriteValue(sb, value, 2, 0);
      return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object v, int indent, int depth) {
      if (v == null) { sb.Append("null"); return; }

      var map = v as IDictionary<string, object>;
      if (map != null) { WriteObj(sb, map, indent, depth); return; }

      var list = v as System.Collections.IEnumerable;
      if (list != null && !(v is string)) { WriteArr(sb, list, indent, depth); return; }

      if (v is string) { WriteString(sb, (string)v); return; }
      if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

      // Every numeric type funnels through double so round-tripping a settings
      // file never turns 1.0 into "1" and back into an int somewhere else.
      double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
      if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
      if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
        sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
      else
        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    static void WriteObj(StringBuilder sb, IDictionary<string, object> map, int indent, int depth) {
      if (map.Count == 0) { sb.Append("{}"); return; }
      sb.Append('{');
      bool first = true;
      foreach (KeyValuePair<string, object> kv in map) {
        if (!first) sb.Append(',');
        first = false;
        NewLine(sb, indent, depth + 1);
        WriteString(sb, kv.Key);
        sb.Append(':');
        if (indent >= 0) sb.Append(' ');
        WriteValue(sb, kv.Value, indent, depth + 1);
      }
      NewLine(sb, indent, depth);
      sb.Append('}');
    }

    static void WriteArr(StringBuilder sb, System.Collections.IEnumerable list, int indent, int depth) {
      sb.Append('[');
      bool first = true;
      foreach (object item in list) {
        if (!first) sb.Append(',');
        first = false;
        NewLine(sb, indent, depth + 1);
        WriteValue(sb, item, indent, depth + 1);
      }
      if (!first) NewLine(sb, indent, depth);
      sb.Append(']');
    }

    static void NewLine(StringBuilder sb, int indent, int depth) {
      if (indent < 0) return;
      sb.Append('\n');
      sb.Append(' ', indent * depth);
    }

    static void WriteString(StringBuilder sb, string s) {
      sb.Append('"');
      for (int i = 0; i < s.Length; i++) {
        char c = s[i];
        switch (c) {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\b': sb.Append("\\b"); break;
          case '\f': sb.Append("\\f"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
            break;
        }
      }
      sb.Append('"');
    }

    // ------------------------------------------------------------ accessors
    //
    // Typed reads that never throw. Callers are pulling values out of a file
    // the user could have hand-edited, or out of a message from the browser,
    // so a wrong type has to degrade to the default rather than take the app
    // down.

    public static Dictionary<string, object> Obj(object v) {
      return v as Dictionary<string, object>;
    }

    public static List<object> Arr(object v) {
      return v as List<object>;
    }

    public static object Get(IDictionary<string, object> map, string key) {
      object v;
      if (map != null && map.TryGetValue(key, out v)) return v;
      return null;
    }

    public static string Str(IDictionary<string, object> map, string key, string fallback) {
      object v = Get(map, key);
      if (v is string) return (string)v;
      if (v == null) return fallback;
      return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static double Num(IDictionary<string, object> map, string key, double fallback) {
      object v = Get(map, key);
      if (v is double) return (double)v;
      if (v == null) return fallback;
      double d;
      if (v is string && double.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
      try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (Exception) { return fallback; }
    }

    public static int Int(IDictionary<string, object> map, string key, int fallback) {
      return (int)Math.Round(Num(map, key, fallback));
    }

    public static bool Bool(IDictionary<string, object> map, string key, bool fallback) {
      object v = Get(map, key);
      if (v is bool) return (bool)v;
      if (v is double) return (double)v != 0;
      if (v is string) {
        string s = ((string)v).Trim().ToLowerInvariant();
        if (s == "true" || s == "1") return true;
        if (s == "false" || s == "0") return false;
      }
      return fallback;
    }

    public static Dictionary<string, object> Sub(IDictionary<string, object> map, string key) {
      return Get(map, key) as Dictionary<string, object>;
    }

    public static List<object> List(IDictionary<string, object> map, string key) {
      return Get(map, key) as List<object>;
    }
  }
}
