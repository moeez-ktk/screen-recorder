using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace LightRecorder {

  internal class Progress {
    public long Frames;
    public double Fps;
    public long Bytes;
    public long TimeMs;
    public long Dropped;
    public string Speed = "";
  }

  internal class RecordResult {
    public string File;
    public long DurationMs;
    public string Error;
  }

  /// <summary>
  /// The recording engine.
  ///
  /// The rule that makes long recordings cheap: no video frame ever enters
  /// this process. ffmpeg captures through the Desktop Duplication API, encodes
  /// on the GPU and muxes straight to disk. We hold a few hundred bytes of
  /// progress text and a bounded ring of PCM audio, so memory is flat whether
  /// the recording lasts ten seconds or ten hours.
  ///
  /// Audio arrives from the WASAPI engine as 48 kHz s16le stereo and is fed to
  /// ffmpeg over a named pipe. That leaves ffmpeg's stdin free, which is how we
  /// can send 'q' for a clean, fully-flushed shutdown instead of killing it,
  /// and how pause and resume reach the filter graph while it runs.
  /// </summary>
  internal class Recorder {

    public enum RecState { Idle, Starting, Recording, Stopping }

    public RecState State = RecState.Idle;
    public string OutFile;
    public DateTime StartedAt;
    public Progress LastProgress;

    /// <summary>True when the last start used the zero-copy GPU pipeline.
    /// Surfaced in the log so a slow recording can be diagnosed.</summary>
    public bool UsedGpuPath;

    public event Action<Progress> ProgressChanged;
    public event Action<RecordResult> Stopped;
    public event Action<string, string[]> Started;   // file, args

    /// <summary>ffmpeg has opened the audio pipe and is ready to read. This is
    /// the moment the audio timeline should begin, not a second earlier when
    /// the process was merely spawned.</summary>
    public event Action AudioPipeConnected;

    public bool IsRecording {
      get { return State == RecState.Recording || State == RecState.Starting; }
    }

    Process _proc;
    NamedPipeServerStream _pipe;
    Thread _pipeWriter;
    volatile bool _pipeAlive;

    readonly Queue<byte[]> _queue = new Queue<byte[]>();
    readonly Queue<int> _queueLengths = new Queue<int>();
    readonly Stack<byte[]> _pool = new Stack<byte[]>();
    readonly object _queueGate = new object();
    int _queuedBytes;

    /// <summary>~1 s of PCM. Beyond this we drop rather than queue: a stalled
    /// pipe must never turn into unbounded memory growth.</summary>
    const int QueueLimit = AudioEngine.Rate * AudioEngine.Channels * 2;

    readonly List<string> _stderrTail = new List<string>();
    Timer _killTimer;
    ManualResetEvent _exited;
    RecordResult _result;
    static int _pipeSeq;

    // ------------------------------------------------------------ arguments

    /// <summary>
    /// Build the ffmpeg command line.
    ///
    /// The important decision is made in one place: when ddagrab is available,
    /// the chosen encoder takes D3D11 textures, and no scaling was asked for,
    /// the frames never leave the GPU. Anything else falls back to the download
    /// path, which is correct but costs roughly sixty times the CPU.
    /// </summary>
    public static string[] BuildArgs(Source source, Settings s, EncoderInfo encoder,
                                     string audioPipe, string outFile, bool hasDdagrab,
                                     double anchorSeconds, out bool gpuDirect) {
      int fps = s.Fps < 1 ? 1 : (s.Fps > 240 ? 240 : s.Fps);
      int cursor = s.CaptureCursor ? 1 : 0;

      var a = new List<string> {
        "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1",
        // ffmpeg reads stdin once per stats period, and pause and resume
        // travel down stdin. At the default half second that lag is visible:
        // up to half a second of video past the pause point, against audio
        // that stopped on the instant.
        "-stats_period", "0.1",
        // Keep each input's own timestamps instead of restarting every one at
        // zero; the capture chain lines them up itself. See PtsFilter.
        "-copyts"
      };
      string pts = PtsFilter(anchorSeconds);

      bool scaling = !string.IsNullOrEmpty(s.Resolution) && s.Resolution != "source";
      // Windows are captured as a region of their monitor, so they take the
      // same GPU path as a screen. See Sources.ResolveWindow.
      bool window = source.Kind == "window";
      bool useDda = hasDdagrab && (source.Kind == "screen" || (window && source.Region.Width > 0));
      gpuDirect = useDda && !scaling && encoder != null && encoder.AcceptsD3D11;

      // Hardware encoders want NV12 (semi-planar) and genuinely fail on planar
      // yuv420p: NVENC reports "CreateInputBuffer failed: invalid param" and
      // writes nothing at all. Only libx264 gets the conventional yuv420p.
      string pixFmt = (encoder != null && encoder.Id == "libx264") ? "yuv420p" : "nv12";

      // Always land on even dimensions; H.264 4:2:0 requires it. min() means a
      // 1080p monitor stays 1080p even when 1440p is selected.
      string scale;
      if (scaling) {
        int h;
        if (!int.TryParse(s.Resolution, NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) h = 1080;
        scale = "scale=-2:'min(" + h + ",ih)':flags=bicubic";
      } else {
        scale = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
      }

      // Video is input 0 on every path, so audio, when there is any, is 1.
      string videoFilter;

      if (useDda) {
        // ddagrab runs on the GPU through DXGI Desktop Duplication, which is
        // what makes fullscreen game capture nearly free next to GDI.
        //
        // It is opened as a lavfi *input* rather than written as a source
        // inside -filter_complex, and that is what makes pausing possible: a
        // graph with no inputs never reads ffmpeg's command queue - only a
        // graph waiting on an input does - so the setpts at the end of the
        // chain would never hear a pause. It is otherwise the same pipeline,
        // measured: D3D11 frames still reach the encoder without a copy.
        var opts = new List<string> {
          "output_idx=" + source.OutputIdx,
          "framerate=" + fps,
          "draw_mouse=" + cursor
        };
        if (window) {
          Rectangle r = source.Region;
          opts.Add("offset_x=" + r.X);
          opts.Add("offset_y=" + r.Y);
          opts.Add("video_size=" + r.Width + "x" + r.Height);
        }
        // Stamped with the wall clock as each frame is read, which is the
        // moment it was captured. ddagrab's own timestamps start from zero at
        // its first frame and say nothing about when that was.
        a.Add("-use_wallclock_as_timestamps"); a.Add("1");
        a.Add("-f"); a.Add("lavfi");
        a.Add("-i"); a.Add("ddagrab=" + string.Join(":", opts.ToArray()));

        if (gpuDirect) {
          // Nothing but setpts, which only rewrites timestamps. The D3D11
          // texture goes straight into the encoder: no hwdownload, no CPU
          // colour conversion, no upload back.
          videoFilter = "[0:v]" + pts + "[v]";
        } else {
          // hwdownload is the single GPU->CPU copy; converting down from BGRA
          // afterwards halves what the encoder has to chew through.
          videoFilter = "[0:v]hwdownload,format=bgra," + scale + ",format=" + pixFmt + "," + pts + "[v]";
        }
      } else {
        // Window capture, and the fallback when ddagrab is unavailable.
        a.Add("-f"); a.Add("gdigrab");
        a.Add("-framerate"); a.Add(fps.ToString(CultureInfo.InvariantCulture));
        a.Add("-draw_mouse"); a.Add(cursor.ToString(CultureInfo.InvariantCulture));
        a.Add("-thread_queue_size"); a.Add("512");

        if (window && source.Handle != IntPtr.Zero) {
          // By handle, not title: a browser's title changes with every tab
          // switch, and gdigrab then cannot find the window at all.
          a.Add("-i"); a.Add("hwnd=" + source.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        } else {
          a.Add("-i"); a.Add("desktop");
        }
        // gdigrab stamps its frames with the wall clock already.
        videoFilter = "[0:v]" + scale + ",format=" + pixFmt + "," + pts + "[v]";
      }

      if (audioPipe != null) {
        a.Add("-f"); a.Add("s16le");
        a.Add("-ar"); a.Add(AudioEngine.Rate.ToString(CultureInfo.InvariantCulture));
        a.Add("-ac"); a.Add(AudioEngine.Channels.ToString(CultureInfo.InvariantCulture));
        a.Add("-channel_layout"); a.Add("stereo");
        a.Add("-thread_queue_size"); a.Add("1024");
        // Everything about this stream is already stated above, so there is
        // nothing to discover by reading it. Without these, ffmpeg spends its
        // default analyseduration buffering PCM before it will start the filter
        // graph - and the screen capture, which lives in that graph, does not
        // begin until it does. Measured at about two and a half seconds of
        // missing footage at the head of every recording.
        a.Add("-analyzeduration"); a.Add("0");
        a.Add("-probesize"); a.Add("32");
        a.Add("-i"); a.Add(audioPipe);
      }

      a.Add("-filter_complex"); a.Add(videoFilter);
      a.Add("-map"); a.Add("[v]");

      if (audioPipe != null) {
        a.Add("-map"); a.Add("1:a");
        // Guard against clock drift between the audio device and the system
        // clock. Over a multi-hour recording this is the difference between
        // perfect sync and audio sliding a second late.
        a.Add("-af"); a.Add("aresample=async=1:min_hard_comp=0.100:first_pts=0");
        a.Add("-c:a"); a.Add("aac");
        a.Add("-b:a"); a.Add((s.AudioBitrateKbps <= 0 ? 160 : s.AudioBitrateKbps) + "k");
        a.Add("-ar"); a.Add(AudioEngine.Rate.ToString(CultureInfo.InvariantCulture));
      } else {
        a.Add("-an");
      }

      string encId = encoder != null ? encoder.Id : "libx264";
      int gop = fps * 2;
      int cq = s.Quality < 1 ? 1 : (s.Quality > 51 ? 51 : s.Quality);
      bool byBitrate = s.QualityMode == "bitrate";
      int kbps = Math.Max(1000, s.BitrateMbps * 1000);

      a.Add("-c:v"); a.Add(encId);
      a.Add("-g"); a.Add(gop.ToString(CultureInfo.InvariantCulture));

      switch (encId) {
        case "h264_nvenc":
          a.AddRange(new[] { "-preset", "p4", "-tune", "hq", "-profile:v", "high", "-bf", "2" });
          if (byBitrate)
            a.AddRange(new[] { "-rc", "cbr", "-b:v", kbps + "k", "-maxrate", kbps + "k", "-bufsize", (kbps * 2) + "k" });
          else
            a.AddRange(new[] { "-rc", "vbr", "-cq", cq.ToString(CultureInfo.InvariantCulture),
                               "-b:v", "0", "-maxrate", (kbps * 2) + "k", "-bufsize", (kbps * 4) + "k" });
          break;

        case "h264_qsv":
          a.AddRange(new[] { "-preset", "medium" });
          if (byBitrate) a.AddRange(new[] { "-b:v", kbps + "k", "-maxrate", kbps + "k" });
          else a.AddRange(new[] { "-global_quality", cq.ToString(CultureInfo.InvariantCulture), "-look_ahead", "0" });
          break;

        case "h264_amf":
          a.AddRange(new[] { "-quality", "balanced" });
          if (byBitrate) a.AddRange(new[] { "-rc", "cbr", "-b:v", kbps + "k" });
          else a.AddRange(new[] { "-rc", "cqp",
                                  "-qp_i", cq.ToString(CultureInfo.InvariantCulture),
                                  "-qp_p", (cq + 2).ToString(CultureInfo.InvariantCulture),
                                  "-qp_b", (cq + 4).ToString(CultureInfo.InvariantCulture) });
          break;

        case "h264_mf":
          a.AddRange(new[] { "-rate_control", byBitrate ? "cbr" : "quality" });
          if (byBitrate) a.AddRange(new[] { "-b:v", kbps + "k" });
          else {
            int q = 100 - cq * 2;
            if (q < 0) q = 0; if (q > 100) q = 100;
            a.AddRange(new[] { "-quality", q.ToString(CultureInfo.InvariantCulture) });
          }
          break;

        default:  // libx264
          a.AddRange(new[] { "-preset", "veryfast", "-profile:v", "high" });
          if (byBitrate)
            a.AddRange(new[] { "-b:v", kbps + "k", "-maxrate", kbps + "k", "-bufsize", (kbps * 2) + "k" });
          else
            a.AddRange(new[] { "-crf", cq.ToString(CultureInfo.InvariantCulture) });
          break;
      }

      // Fragmented MP4: the file on disk is valid and playable at every
      // instant, so a crash or a power cut costs the last fragment rather than
      // the whole recording. Stopping is also instant - there is no muxing wait
      // proportional to length.
      a.AddRange(new[] {
        "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
        "-max_muxing_queue_size", "2048",
        "-f", "mp4", "-y", outFile
      });

      return a.ToArray();
    }

    // ---------------------------------------------------------------- start

    public bool Start(Source source, Settings s, EncoderInfo encoder, string ffmpeg,
                      bool hasDdagrab, bool wantAudio, out string error) {
      error = null;
      if (IsRecording) { error = "Already recording"; return false; }

      State = RecState.Starting;
      _stderrTail.Clear();
      LastProgress = null;
      _result = null;
      _paused = false;
      _pausedTicks = 0;
      _exited = new ManualResetEvent(false);

      try {
        Directory.CreateDirectory(s.OutputDir);
      } catch (Exception err) {
        State = RecState.Idle;
        error = "Cannot write to " + s.OutputDir + " (" + err.Message + ")";
        return false;
      }

      OutFile = Settings.UniquePath(s.OutputDir, s.BuildFilename(s.Container));

      string audioPipe = null;
      if (wantAudio) audioPipe = CreateAudioPipe();

      // The instant both tracks count from, read on both clocks it is needed
      // on, immediately before ffmpeg starts. See PtsFilter.
      AnchorTicks = Stopwatch.GetTimestamp();
      _anchorSeconds = Native.PreciseEpochSeconds();

      bool gpu;
      string[] args = BuildArgs(source, s, encoder, audioPipe, OutFile, hasDdagrab, _anchorSeconds, out gpu);
      UsedGpuPath = gpu;

      try {
        var psi = new ProcessStartInfo(ffmpeg, JoinArgs(args));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        _proc = new Process();
        _proc.StartInfo = psi;
        _proc.EnableRaisingEvents = true;
        _proc.OutputDataReceived += OnProgressLine;
        _proc.ErrorDataReceived += OnStderrLine;
        _proc.Exited += OnExited;

        if (!_proc.Start()) throw new Exception("ffmpeg would not start");
        // If this process dies without stopping cleanly, ffmpeg goes with it
        // rather than recording the screen to disk forever.
        Native.KillWithUs(_proc.Handle);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
      } catch (Exception err) {
        State = RecState.Idle;
        TeardownPipe();
        error = "Could not launch ffmpeg: " + err.Message;
        return false;
      }

      StartedAt = DateTime.UtcNow;
      State = RecState.Recording;

      Action<string, string[]> h = Started;
      if (h != null) h(OutFile, args);
      return true;
    }

    // ----------------------------------------------------------------- pause

    /// <summary>
    /// The filter every capture chain ends in, and the reason the two tracks
    /// line up.
    ///
    /// Video timestamps arrive as wall-clock times: gdigrab stamps frames that
    /// way itself, and ddagrab is read with -use_wallclock_as_timestamps.
    /// Subtracting the anchor - the instant ffmpeg was launched, which is also
    /// where the audio pacer's timeline begins - puts each frame at the same
    /// timestamp as the sound that happened with it, however long ffmpeg took
    /// to open each input. Left to itself, ffmpeg starts every input's clock
    /// at that input's first packet; on the ddagrab path that was measured
    /// putting the picture 150 ms behind the sound.
    ///
    /// Pausing swaps the expression at runtime, over stdin, for one that
    /// pushes each arriving frame far into the past, where ffmpeg's frame-rate
    /// sync discards it before it reaches the encoder. Resuming swaps in one
    /// that also takes off the time spent paused, so the file carries straight
    /// on: no gap, no frozen frame, one encoder session, one file, and
    /// stopping stays instant.
    ///
    /// ffmpeg only reads its command queue every hundred milliseconds or so,
    /// and applying the swap the moment it arrives would cut the video that
    /// much later than the audio, by an amount that differs at every pause -
    /// a random tenth of a second of drift per pause. So neither edge is "now".
    /// Each expression names the wall-clock instant it takes effect at, a
    /// quarter of a second ahead, and decides per frame from the frame's own
    /// capture time; the audio pacer is given the same instant. Both tracks
    /// then cut at the identical moment however late ffmpeg got round to it.
    /// </summary>
    static string PtsFilter(double offsetSeconds) { return "setpts=" + PtsExpr(offsetSeconds); }

    static string PtsExpr(double offsetSeconds) {
      return "PTS-" + Sec(offsetSeconds) + "/TB";
    }

    /// <summary>Frames captured from <paramref name="at"/> on get one mapping,
    /// earlier ones the other. T is the frame's capture time in seconds. Only
    /// ever sent as a runtime command, where the argument is the rest of the
    /// line and commas need no escaping - inside -filter_complex they would.</summary>
    static string SplitPtsExpr(double at, double offsetBefore, double offsetAfter) {
      return "if(gte(T," + Sec(at) + "),PTS-" + Sec(offsetAfter) + "/TB,PTS-" + Sec(offsetBefore) + "/TB)";
    }

    static string Sec(double seconds) { return seconds.ToString("0.000000", CultureInfo.InvariantCulture); }

    /// <summary>How far ahead a pause or resume edge is placed. Comfortably
    /// past the worst case of ffmpeg's command polling, and still short enough
    /// that the button feels immediate.</summary>
    public const int EdgeLeadMs = 250;

    /// <summary>A million seconds behind where the frame belongs: far behind
    /// anything already written.</summary>
    const double Discard = 1000000;

    /// <summary>The instant both tracks count from, on the two clocks that
    /// need it: Stopwatch ticks for the audio pacer, wall-clock seconds for
    /// ffmpeg's frame timestamps.</summary>
    public long AnchorTicks;
    double _anchorSeconds;

    bool _paused;
    long _pauseStarted;                  // Stopwatch timestamp of the pause edge
    long _pausedTicks;                   // finished pauses, in Stopwatch ticks

    public bool IsPaused { get { return _paused; } }

    /// <summary>
    /// Pause from <paramref name="edgeTicks"/> (a Stopwatch timestamp, a
    /// little in the future) - the same instant the audio pacer is told to
    /// stop at. <paramref name="edgeSeconds"/> is that instant on the wall
    /// clock the frames are stamped with.
    /// </summary>
    public bool Pause(long edgeTicks, double edgeSeconds) {
      if (State != RecState.Recording || _paused) return false;
      _paused = true;
      _pauseStarted = edgeTicks;
      double offset = _anchorSeconds + (double)_pausedTicks / Stopwatch.Frequency;
      SendPtsExpression(SplitPtsExpr(edgeSeconds, offset, _anchorSeconds + Discard));
      return true;
    }

    public bool Resume(long edgeTicks, double edgeSeconds) {
      if (State != RecState.Recording || !_paused) return false;
      _pausedTicks += edgeTicks - _pauseStarted;
      _paused = false;
      // Measured on the clock the audio pacer stops and starts, so both
      // tracks leave out the same amount of time.
      double offset = _anchorSeconds + (double)_pausedTicks / Stopwatch.Frequency;
      SendPtsExpression(SplitPtsExpr(edgeSeconds, _anchorSeconds + Discard, offset));
      return true;
    }

    /// <summary>ffmpeg's interactive 'c' command: target, time (-1 is now),
    /// command and argument, on one line.</summary>
    void SendPtsExpression(string expr) {
      try {
        _proc.StandardInput.Write("csetpts -1 expr " + expr + "\n");
        _proc.StandardInput.Flush();
      } catch (Exception err) {
        Log.Warn("pause: could not reach ffmpeg: " + err.Message);
      }
    }

    /// <summary>Time actually recorded: since the start, less every pause,
    /// including one in progress.</summary>
    long ActiveMs() {
      long paused = _pausedTicks;
      if (_paused) paused += Math.Max(0, Stopwatch.GetTimestamp() - _pauseStarted);
      return (long)(DateTime.UtcNow - StartedAt).TotalMilliseconds - paused * 1000 / Stopwatch.Frequency;
    }

    // ------------------------------------------------------------ audio pipe

    string CreateAudioPipe() {
      string name = "lightrec-audio-" + Process.GetCurrentProcess().Id + "-" + (++_pipeSeq);
      try {
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                                          PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                          0, 1 << 18);
        // ffmpeg opens this by name, so it has no business inheriting the
        // server end of it.
        Native.DontInherit(_pipe.SafePipeHandle.DangerousGetHandle());
        _pipeAlive = true;
        _pipeWriter = new Thread(PipeWriterLoop);
        _pipeWriter.IsBackground = true;
        _pipeWriter.Name = "audio-pipe";
        _pipeWriter.Start();
        return @"\\.\pipe\" + name;
      } catch (Exception err) {
        Log.Warn("audio pipe failed: " + err.Message);
        _pipe = null;
        return null;
      }
    }

    void PipeWriterLoop() {
      NamedPipeServerStream pipe = _pipe;
      if (pipe == null) return;

      try {
        // ffmpeg opens the pipe as an input file a moment after it starts.
        IAsyncResult ar = pipe.BeginWaitForConnection(null, null);
        if (!ar.AsyncWaitHandle.WaitOne(10000)) return;
        pipe.EndWaitForConnection(ar);
      } catch (Exception) {
        return;
      }

      // Worth having when sync is in question: this is how much silence opens
      // the audio track, standing in for the time ffmpeg took to get going.
      Log.AppendRecording("audio   : pipe opened " +
        ((Stopwatch.GetTimestamp() - AnchorTicks) * 1000 / Stopwatch.Frequency) + " ms after launch\n");

      Action connected = AudioPipeConnected;
      if (connected != null) {
        try { connected(); } catch (Exception) { }
      }

      while (_pipeAlive) {
        if (!WriteOne(pipe)) Thread.Sleep(4);
      }

      // Drain whatever the pacer produced before the stop, so the tail of the
      // recording is not clipped. Bounded, so a pacer that somehow keeps
      // producing cannot hold the shutdown open.
      for (int i = 0; i < 512 && WriteOne(pipe); i++) { }

      // Then close, which is what gives ffmpeg EOF on the audio input. Without
      // it ffmpeg sits blocked on a read that will never complete, ignores the
      // 'q' we just sent, and has to be killed - costing eight seconds and a
      // hard shutdown on every single recording.
      try { pipe.Flush(); } catch (Exception) { }
      try { if (pipe.IsConnected) pipe.Disconnect(); } catch (Exception) { }
      try { pipe.Dispose(); } catch (Exception) { }
    }

    /// <summary>Write one queued block. Returns false when the queue is empty
    /// or the pipe has gone.</summary>
    bool WriteOne(NamedPipeServerStream pipe) {
      byte[] block = null;
      int length = 0;
      lock (_queueGate) {
        if (_queue.Count > 0) {
          block = _queue.Dequeue();
          length = _queueLengths.Dequeue();
          _queuedBytes -= length;
        }
      }
      if (block == null) return false;

      try {
        pipe.Write(block, 0, length);
      } catch (Exception) {
        // ffmpeg exiting closes this. Not an error we act on.
        _pipeAlive = false;
      }

      lock (_queueGate) { if (_pool.Count < 32) _pool.Push(block); }
      return true;
    }

    /// <summary>
    /// Called from the audio pacer with a buffer it is about to reuse, so the
    /// bytes are copied into a pooled block. The pool means a three-hour
    /// recording allocates nothing after the first second.
    /// </summary>
    public void WriteAudio(byte[] data, int count) {
      if (count <= 0) return;
      if (!IsRecording && State != RecState.Stopping) return;
      if (_pipe == null) return;

      lock (_queueGate) {
        byte[] block = null;
        while (_pool.Count > 0) {
          byte[] candidate = _pool.Pop();
          if (candidate.Length >= count) { block = candidate; break; }
        }
        if (block == null) block = new byte[Math.Max(count, 16384)];

        Buffer.BlockCopy(data, 0, block, 0, count);
        _queue.Enqueue(block);
        _queueLengths.Enqueue(count);
        _queuedBytes += count;

        // Never queue without a ceiling: if ffmpeg has stalled, dropping the
        // oldest audio is the only bounded answer.
        while (_queuedBytes > QueueLimit && _queue.Count > 0) {
          byte[] old = _queue.Dequeue();
          _queuedBytes -= _queueLengths.Dequeue();
          if (_pool.Count < 32) _pool.Push(old);
        }
      }
    }

    void TeardownPipe() {
      _pipeAlive = false;
      Thread t = _pipeWriter;
      _pipeWriter = null;
      if (t != null) { try { t.Join(400); } catch (Exception) { } }

      NamedPipeServerStream p = _pipe;
      _pipe = null;
      if (p != null) {
        try { if (p.IsConnected) p.Disconnect(); } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
      }

      lock (_queueGate) {
        _queue.Clear();
        _queueLengths.Clear();
        _pool.Clear();
        _queuedBytes = 0;
      }
    }

    // -------------------------------------------------------------- streams

    void OnProgressLine(object sender, DataReceivedEventArgs e) {
      // -progress pipe:1 emits key=value lines, which is far cheaper to parse
      // than scraping the human-readable stats block off stderr.
      if (e.Data == null) return;
      string line = e.Data;
      int eq = line.IndexOf('=');
      if (eq <= 0) return;

      string key = line.Substring(0, eq).Trim();
      string value = line.Substring(eq + 1).Trim();

      Progress p = LastProgress ?? new Progress();
      switch (key) {
        case "frame": p.Frames = ParseLong(value); break;
        case "fps": p.Fps = ParseDouble(value); break;
        case "total_size": p.Bytes = ParseLong(value); break;
        case "out_time_us": p.TimeMs = ParseLong(value) / 1000; break;
        case "out_time_ms": if (p.TimeMs == 0) p.TimeMs = ParseLong(value) / 1000; break;
        case "drop_frames": p.Dropped = ParseLong(value); break;
        case "speed": p.Speed = value; break;
        case "progress":
          // Emitted last in each block, so this is the point at which the
          // snapshot is complete.
          LastProgress = p;
          Action<Progress> h = ProgressChanged;
          if (h != null) h(p);
          return;
      }
      LastProgress = p;
    }

    static long ParseLong(string s) {
      long v;
      return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    static double ParseDouble(string s) {
      double v;
      return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    void OnStderrLine(object sender, DataReceivedEventArgs e) {
      if (e.Data == null) return;
      string line = e.Data.Trim();
      if (line.Length == 0) return;
      // Pause and resume go through ffmpeg's interactive command prompt, which
      // answers on stderr. Those lines are not problems, and keeping them
      // would push a real error out of the tail - unless the reply says the
      // command was refused, which is worth knowing about.
      if (line.StartsWith("Enter command:", StringComparison.Ordinal)) return;
      if (line.StartsWith("Command reply", StringComparison.Ordinal)) {
        if (line.IndexOf("ret:0", StringComparison.Ordinal) < 0) Log.AppendRecording("pause   : ffmpeg refused a command: " + line + "\n");
        return;
      }
      lock (_stderrTail) {
        _stderrTail.Add(e.Data);
        if (_stderrTail.Count > 40) _stderrTail.RemoveAt(0);   // bounded: never grows
      }
    }

    string StderrText() {
      lock (_stderrTail) return string.Join("\n", _stderrTail.ToArray());
    }

    // ----------------------------------------------------------------- exit

    void OnExited(object sender, EventArgs e) {
      bool wasStopping = State == RecState.Stopping;
      int code = -1;
      try { code = _proc.ExitCode; } catch (Exception) { }

      State = RecState.Idle;
      if (_killTimer != null) { _killTimer.Dispose(); _killTimer = null; }
      TeardownPipe();

      bool failed = code != 0 && !wasStopping;
      var result = new RecordResult();
      result.File = OutFile;
      result.DurationMs = ActiveMs();
      _paused = false;
      result.Error = failed
        ? (StderrText().Length > 0 ? StderrText() : "ffmpeg exited with code " + code)
        : null;

      _result = result;
      try { if (_exited != null) _exited.Set(); } catch (Exception) { }

      Action<RecordResult> h = Stopped;
      if (h != null) h(result);
    }

    /// <summary>
    /// Ask ffmpeg to finish. Blocks up to <paramref name="timeoutMs"/> for the
    /// file to be finalised, then returns whatever happened.
    /// </summary>
    public RecordResult Stop(int timeoutMs) {
      if (_proc == null || State == RecState.Idle) return _result;

      if (State != RecState.Stopping) {
        State = RecState.Stopping;

        // 'q' makes ffmpeg flush the encoder and finalise the mp4 properly.
        try {
          _proc.StandardInput.Write('q');
          _proc.StandardInput.Flush();
          _proc.StandardInput.Close();
        } catch (Exception) { /* already gone */ }

        // Closing the pipe lets ffmpeg see EOF on the audio input.
        _pipeAlive = false;

        // If it will not go quietly, kill it. Fragmented MP4 means the file on
        // disk is still playable even then.
        _killTimer = new Timer(delegate {
          try {
            if (_proc != null && State == RecState.Stopping && !_proc.HasExited) _proc.Kill();
          } catch (Exception) { }
        }, null, 8000, Timeout.Infinite);
      }

      try { if (_exited != null) _exited.WaitOne(timeoutMs); } catch (Exception) { }
      return _result;
    }

    public long ElapsedMs {
      get { return IsRecording ? ActiveMs() : 0; }
    }

    // ------------------------------------------------------------- quoting

    /// <summary>
    /// Join arguments the way CommandLineToArgvW will take them apart again.
    /// Filter graphs are full of quotes and brackets and save folders are full
    /// of spaces, so this is not somewhere to improvise.
    /// </summary>
    public static string JoinArgs(string[] args) {
      var sb = new StringBuilder();
      for (int i = 0; i < args.Length; i++) {
        if (i > 0) sb.Append(' ');
        sb.Append(QuoteArg(args[i]));
      }
      return sb.ToString();
    }

    public static string QuoteArg(string arg) {
      if (arg == null) return "\"\"";
      if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return arg;

      var sb = new StringBuilder();
      sb.Append('"');
      int backslashes = 0;
      foreach (char c in arg) {
        if (c == '\\') { backslashes++; continue; }
        if (c == '"') {
          // Backslashes immediately before a quote must themselves be doubled.
          sb.Append('\\', backslashes * 2 + 1);
          backslashes = 0;
          sb.Append('"');
          continue;
        }
        sb.Append('\\', backslashes);
        backslashes = 0;
        sb.Append(c);
      }
      sb.Append('\\', backslashes * 2);
      sb.Append('"');
      return sb.ToString();
    }

    // -------------------------------------------------------------- remux

    /// <summary>
    /// Optional post-pass: rewrite the fragmented MP4 into a normal one with
    /// the moov atom up front. Stream copy only, so it is I/O bound rather than
    /// a re-encode. Off by default because it makes stopping non-instant.
    /// </summary>
    public static string Remux(string ffmpeg, string file) {
      string tmp = file.Substring(0, file.Length - 4) + ".tmp.mp4";
      try {
        var psi = new ProcessStartInfo(ffmpeg, JoinArgs(new[] {
          "-hide_banner", "-loglevel", "error", "-nostdin",
          "-i", file, "-c", "copy", "-movflags", "+faststart", "-y", tmp
        }));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using (Process p = Process.Start(psi)) {
          if (!p.WaitForExit(300000)) { try { p.Kill(); } catch (Exception) { } return "remux timed out"; }
          if (p.ExitCode != 0 || !File.Exists(tmp)) {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            return "remux exited " + p.ExitCode;
          }
        }

        File.Delete(file);
        File.Move(tmp, file);
        return null;
      } catch (Exception err) {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        return err.Message;
      }
    }
  }
}
