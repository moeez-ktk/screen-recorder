using System;
using System.IO;
using System.Text;

namespace LightRecorder {

  /// <summary>
  /// Two logs, both bounded, both only written when something is worth saying.
  ///
  ///   app.log             warnings and failures, trimmed to the last 64 KB
  ///   last-recording.log  the exact ffmpeg command line and anything it
  ///                       complained about, rewritten per recording
  ///
  /// A failed encode otherwise leaves nothing but a zero-byte file, which is
  /// impossible to diagnose after the fact.
  /// </summary>
  internal static class Log {
    const long MaxBytes = 64 * 1024;
    static readonly object Gate = new object();

    static string File_ { get { return Path.Combine(Paths.UserData, "app.log"); } }

    public static void Warn(string message) {
      Write("WARN", message);
    }

    public static void Info(string message) {
      Write("INFO", message);
    }

    static void Write(string level, string message) {
      lock (Gate) {
        try {
          string path = File_;
          // Truncate rather than rotate: nobody wants a log directory, and the
          // interesting lines are always the most recent ones.
          if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
          File.AppendAllText(path,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + level + "  " + message +
            Environment.NewLine, Encoding.UTF8);
        } catch (Exception) { /* logging must never break the app */ }
      }
    }

    public static void Recording(string text) {
      try { File.WriteAllText(Paths.RecordingLog, text, Encoding.UTF8); }
      catch (Exception) { }
    }

    public static void AppendRecording(string text) {
      try { File.AppendAllText(Paths.RecordingLog, text, Encoding.UTF8); }
      catch (Exception) { }
    }
  }
}
