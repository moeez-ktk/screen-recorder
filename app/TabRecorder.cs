using System;
using System.Diagnostics;
using System.IO;

namespace LightRecorder {

  /// <summary>
  /// Writes a browser-tab recording to disk.
  ///
  /// When the chosen source is a single tab, ffmpeg is not involved. Only the
  /// browser can capture one tab in isolation - and capture only that tab's
  /// audio, which is the whole point - so the extension encodes with
  /// MediaRecorder and streams chunks over the local bridge. Our only job is to
  /// append them as they arrive, so nothing accumulates in memory.
  /// </summary>
  internal class TabRecorder {

    public Recorder.RecState State = Recorder.RecState.Idle;
    public string OutFile;
    public long Bytes;
    public DateTime StartedAt;

    public event Action<Progress> ProgressChanged;
    public event Action<string> Failed;

    FileStream _stream;
    string _actualExt;

    public bool IsRecording {
      get { return State == Recorder.RecState.Recording || State == Recorder.RecState.Starting; }
    }

    public long ElapsedMs {
      get { return IsRecording ? ActiveMs() : 0; }
    }

    // The browser does the actual pausing; this only keeps the clock honest.
    bool _paused;
    long _pauseStarted, _pausedTicks;

    public bool IsPaused { get { return _paused; } }

    public void Pause() {
      if (State != Recorder.RecState.Recording || _paused) return;
      _paused = true;
      _pauseStarted = Stopwatch.GetTimestamp();
    }

    public void Resume() {
      if (!_paused) return;
      _pausedTicks += Stopwatch.GetTimestamp() - _pauseStarted;
      _paused = false;
    }

    long ActiveMs() {
      long paused = _pausedTicks + (_paused ? Stopwatch.GetTimestamp() - _pauseStarted : 0);
      return (long)(DateTime.UtcNow - StartedAt).TotalMilliseconds - paused * 1000 / Stopwatch.Frequency;
    }

    public bool Start(Settings s, out string error) {
      error = null;
      if (IsRecording) { error = "Already recording"; return false; }

      try {
        Directory.CreateDirectory(s.OutputDir);
        // The browser picks the container (MP4 when it can, WebM otherwise) and
        // only says which once encoding has begun. Rather than race that, the
        // file opens straight away and the extension is corrected at the end.
        OutFile = Settings.UniquePath(s.OutputDir, s.BuildFilename("mp4"));
        _stream = new FileStream(OutFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
      } catch (Exception err) {
        error = "Could not open the output file: " + err.Message;
        return false;
      }

      Bytes = 0;
      _actualExt = null;
      _paused = false;
      _pausedTicks = 0;
      StartedAt = DateTime.UtcNow;
      State = Recorder.RecState.Recording;
      return true;
    }

    public void Write(byte[] chunk, int count) {
      FileStream s = _stream;
      if (s == null || State == Recorder.RecState.Idle || count <= 0) return;

      try {
        s.Write(chunk, 0, count);
      } catch (Exception err) {
        Action<string> f = Failed;
        if (f != null) f("Write failed: " + err.Message);
        return;
      }

      Bytes += count;
      Action<Progress> h = ProgressChanged;
      if (h != null) {
        var p = new Progress();
        p.Bytes = Bytes;
        p.TimeMs = ActiveMs();
        h(p);
      }
    }

    public void SetContainerFromMime(string mime) {
      if (string.IsNullOrEmpty(mime)) return;
      _actualExt = mime.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0 ? "mp4" : "webm";
    }

    public RecordResult Stop() {
      if (State == Recorder.RecState.Idle) return null;
      State = Recorder.RecState.Stopping;

      FileStream s = _stream;
      _stream = null;
      if (s != null) {
        try { s.Flush(); } catch (Exception) { }
        try { s.Dispose(); } catch (Exception) { }
      }

      // Rename if the browser gave us WebM after we optimistically said mp4.
      if (!string.IsNullOrEmpty(_actualExt) && !string.IsNullOrEmpty(OutFile) &&
          !OutFile.EndsWith("." + _actualExt, StringComparison.OrdinalIgnoreCase)) {
        try {
          string renamed = Path.ChangeExtension(OutFile, "." + _actualExt);
          File.Move(OutFile, renamed);
          OutFile = renamed;
        } catch (Exception) { /* the file is still there under the old name */ }
      }

      var result = new RecordResult();
      result.File = OutFile;
      result.DurationMs = ActiveMs();
      result.Error = Bytes == 0 ? "Nothing was captured from the tab." : null;

      State = Recorder.RecState.Idle;
      _paused = false;
      return result;
    }
  }
}
