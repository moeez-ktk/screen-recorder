using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LightRecorder {

  /// <summary>
  /// The audio engine.
  ///
  ///   system loopback ─▶ ring ────────────┐
  ///                                       ├─▶ pacer ─▶ gain ─▶ limiter ─▶ s16le ─▶ ffmpeg
  ///   microphone      ─▶ ring ─▶ leveler ─┘
  ///
  /// Two capture threads fill fixed-size ring buffers; one pacer thread emits
  /// exactly as many frames as wall-clock time says should have elapsed,
  /// filling silence when a source is dry. That last part is what keeps a
  /// three-hour recording in sync: loopback capture delivers nothing at all
  /// while nothing is playing, so a naive "write whatever arrives" pipeline
  /// slides earlier and earlier against the video.
  ///
  /// Total memory in flight is two 1-second rings and a working buffer -
  /// about 800 KB, and it never grows.
  /// </summary>
  internal class AudioEngine {

    public const int Rate = 48000;
    public const int Channels = 2;

    /// <summary>How long a mute/unmute takes to slide in. A hard gain step is
    /// an audible click in the recording.</summary>
    const double RampSeconds = 0.015;

    /// <summary>Reported once per source when it cannot be opened, so the UI
    /// can say "recording video only" rather than silently losing audio.</summary>
    public event Action<string> Problem;

    readonly object _gate = new object();

    WasapiCapture _system;
    WasapiCapture _mic;
    Thread _pacer;
    volatile bool _running;

    Action<byte[], int> _sink;

    /// <summary>
    /// The pacer holds off until ffmpeg has actually opened the audio pipe.
    ///
    /// Emitting from the moment the devices open sounds harmless but is not:
    /// ffmpeg takes about a second to start, every sample produced in that
    /// window has nowhere to go, and the oldest of them get dropped when the
    /// queue hits its ceiling. The result is an audio track shifted a fraction
    /// of a second against the video - which is exactly the kind of thing
    /// nobody notices until they are editing.
    /// </summary>
    volatile bool _emit;

    // Live gains. Written from the UI thread, read by the pacer; float writes
    // are atomic on every platform we run on, so no lock is warranted here.
    volatile float _systemTarget, _micTarget;
    float _systemNow, _micNow;

    /// <summary>
    /// The pacer's timeline, in Stopwatch ticks. It starts at the anchor - the
    /// instant the recorder launched ffmpeg, which the video timestamps count
    /// from too - and leaves out every pause, so the track simply has no time
    /// in it for one. The video side does the same by discarding frames, and
    /// the two stay aligned across any number of pauses. Guarded because the
    /// UI thread pauses while the pacer is reading.
    /// </summary>
    readonly object _clockGate = new object();
    long _anchor;
    long _pausedTicks, _pauseStarted;
    bool _paused;

    /// <summary>Set on resume, so the first samples after a pause slide in
    /// from silence instead of butting up against the last ones before it -
    /// which would be an audible click at the join.</summary>
    volatile bool _rampFromSilence;

    /// <summary>Kept for the life of the app, so the gain it settles on
    /// carries into the next recording instead of being relearned.</summary>
    readonly VoiceLeveler _leveler = new VoiceLeveler(Rate);
    volatile bool _autoLevel;

    /// <summary>The last stage before the file. Two sources near full scale
    /// add up to more than full scale, and hard-clipping that is harsh.</summary>
    readonly PeakLimiter _limiter = new PeakLimiter(Rate, 0.944f, 0.08);

    public bool IsRunning { get { return _running; } }

    // ------------------------------------------------------------ lifecycle

    /// <summary>
    /// Open whichever sources the settings ask for and start emitting.
    /// Returns false only when nothing at all could be opened; a single failed
    /// source is reported through Problem and the other one carries on.
    /// </summary>
    public bool Start(Settings s, Action<byte[], int> sink) {
      lock (_gate) {
        if (_running) return true;
        _sink = sink;

        _systemTarget = s.EffectiveSystemGain;
        _micTarget = s.EffectiveMicGain;
        // Start at the target rather than ramping up from zero, or every
        // recording would begin with a 15 ms fade-in.
        _systemNow = _systemTarget;
        _micNow = _micTarget;
        _autoLevel = s.MicAutoLevel;
        _leveler.Reset();
        _limiter.Reset();
        lock (_clockGate) { _anchor = 0; _pausedTicks = 0; _paused = false; }

        var problems = new List<string>();

        if (s.SystemAudioEnabled) {
          try {
            _system = WasapiCapture.OpenLoopback();
          } catch (Exception err) {
            problems.Add("system audio unavailable (" + err.Message + ")");
          }
        }

        if (s.MicEnabled) {
          try {
            _mic = WasapiCapture.OpenMicrophone(s.MicDeviceId);
          } catch (Exception err) {
            problems.Add("microphone unavailable (" + err.Message + ")");
          }
        }

        if (_system == null && _mic == null) {
          Report(problems.Count > 0 ? string.Join("; ", problems.ToArray()) : "no audio sources");
          Cleanup();
          return false;
        }
        if (problems.Count > 0) Report(string.Join("; ", problems.ToArray()));

        _running = true;
        _pacer = new Thread(PacerLoop);
        _pacer.IsBackground = true;
        _pacer.Name = "audio-pacer";
        // Just above normal: being late here shows up as a gap in the file,
        // and the work per wake-up is a few thousand multiply-adds.
        _pacer.Priority = ThreadPriority.AboveNormal;
        _pacer.Start();
        return true;
      }
    }

    /// <summary>
    /// ffmpeg is listening: discard whatever the devices buffered while it was
    /// starting and begin emitting. Flushing matters as much as the gate does
    /// - a ring holding a second of stale audio would otherwise put the whole
    /// track a second late. The timeline itself began at the anchor, when
    /// ffmpeg was launched, and the pacer catches up to it with silence.
    /// </summary>
    public void BeginEmitting(long anchorTicks) {
      WasapiCapture sys = _system, mic = _mic;
      if (sys != null) sys.Flush();
      if (mic != null) mic.Flush();
      lock (_clockGate) _anchor = anchorTicks;
      _emit = true;
    }

    /// <summary>Live gain change - takes effect mid-recording without
    /// touching the encoder.</summary>
    public void UpdateGains(Settings s) {
      _systemTarget = s.EffectiveSystemGain;
      _micTarget = s.EffectiveMicGain;
      _autoLevel = s.MicAutoLevel;
    }

    /// <summary>Stop the timeline. Nothing is emitted until Resume, so the
    /// track has no time in it for the pause.</summary>
    public void Pause() {
      lock (_clockGate) {
        if (_paused) return;
        _paused = true;
        _pauseStarted = Stopwatch.GetTimestamp();
      }
    }

    public void Resume() {
      lock (_clockGate) {
        if (!_paused) return;
        // What the devices heard while paused is not part of the recording.
        WasapiCapture sys = _system, mic = _mic;
        if (sys != null) sys.Flush();
        if (mic != null) mic.Flush();
        _pausedTicks += Stopwatch.GetTimestamp() - _pauseStarted;
        _paused = false;
        _rampFromSilence = true;
      }
    }

    /// <summary>Frames of recorded time between the anchor and now.</summary>
    long TimelineFrames() {
      lock (_clockGate) {
        long now = Stopwatch.GetTimestamp();
        long ticks = now - _anchor - _pausedTicks - (_paused ? now - _pauseStarted : 0);
        return (long)((double)ticks * Rate / Stopwatch.Frequency);
      }
    }

    public void Stop() {
      lock (_gate) {
        if (!_running) { Cleanup(); return; }
        _running = false;

        Thread p = _pacer;
        _pacer = null;
        if (p != null) { try { p.Join(500); } catch (Exception) { } }

        Cleanup();
        lock (_clockGate) { _anchor = 0; _pausedTicks = 0; _paused = false; }
      }
    }

    void Cleanup() {
      if (_system != null) { _system.Dispose(); _system = null; }
      if (_mic != null) { _mic.Dispose(); _mic = null; }
      _sink = null;
    }

    void Report(string message) {
      Action<string> h = Problem;
      if (h != null) h(message);
      Log.Warn("audio: " + message);
    }

    // ---------------------------------------------------------------- pacer

    void PacerLoop() {
      const int MaxFrames = 8192;                       // ~170 ms per pass
      var sysBuf = new float[MaxFrames * Channels];
      var micBuf = new float[MaxFrames * Channels];
      var outBuf = new byte[MaxFrames * Channels * 2];

      while (_running && !_emit) Thread.Sleep(2);
      if (!_running) return;

      // The timeline began at the anchor, when ffmpeg was launched, but the
      // pipe only opens once ffmpeg has its inputs up - a few hundred
      // milliseconds later. That stretch goes out as silence straight away,
      // so a sound lands at the same timestamp as the frame it happened in.
      long emitted = 0;
      Array.Clear(outBuf, 0, outBuf.Length);
      for (long lead = TimelineFrames(); lead > 0 && _running; ) {
        int f = (int)Math.Min(lead, MaxFrames);
        Action<byte[], int> leadSink = _sink;
        if (leadSink != null) { try { leadSink(outBuf, f * Channels * 2); } catch (Exception) { } }
        emitted += f;
        lead -= f;
      }

      // How far the gain may travel per frame during a mute/unmute ramp.
      float step = (float)(1.0 / (RampSeconds * Rate));

      while (_running) {
        long target = TimelineFrames();
        long need = target - emitted;

        if (need <= 0) { Thread.Sleep(4); continue; }
        if (need > MaxFrames) need = MaxFrames;

        int frames = (int)need;
        int samples = frames * Channels;

        WasapiCapture sys = _system, mic = _mic;
        if (sys != null) sys.Read(sysBuf, samples); else Array.Clear(sysBuf, 0, samples);
        if (mic != null) {
          mic.Read(micBuf, samples);
          if (_autoLevel) _leveler.Process(micBuf, frames);
        } else {
          Array.Clear(micBuf, 0, samples);
        }

        float sysNow = _systemNow, micNow = _micNow;
        float sysTo = _systemTarget, micTo = _micTarget;
        if (_rampFromSilence) { _rampFromSilence = false; sysNow = 0f; micNow = 0f; }

        int o = 0;
        for (int i = 0; i < samples; i += Channels) {
          // Ramp once per frame, not once per sample, so the two channels
          // never drift apart by a step.
          if (sysNow != sysTo) {
            if (sysNow < sysTo) { sysNow += step; if (sysNow > sysTo) sysNow = sysTo; }
            else { sysNow -= step; if (sysNow < sysTo) sysNow = sysTo; }
          }
          if (micNow != micTo) {
            if (micNow < micTo) { micNow += step; if (micNow > micTo) micNow = micTo; }
            else { micNow -= step; if (micNow < micTo) micNow = micTo; }
          }

          // The engine is fixed at stereo, and the limiter needs both channels
          // of a frame at once so the image does not shift when it acts.
          float l = sysBuf[i] * sysNow + micBuf[i] * micNow;
          float r = sysBuf[i + 1] * sysNow + micBuf[i + 1] * micNow;
          _limiter.Process(ref l, ref r);
          o = WriteSample(outBuf, o, l);
          o = WriteSample(outBuf, o, r);
        }

        _systemNow = sysNow;
        _micNow = micNow;
        emitted += frames;

        Action<byte[], int> sink = _sink;
        if (sink != null) {
          try { sink(outBuf, o); }
          catch (Exception) { /* the pipe closing is normal at end of recording */ }
        }
      }
    }

    /// <summary>One s16le sample. Asymmetric scaling matches the int16 range
    /// exactly, so full-scale negative does not wrap.</summary>
    static int WriteSample(byte[] buf, int o, float v) {
      if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
      int pcm = (int)(v < 0 ? v * 32768f : v * 32767f);
      buf[o] = (byte)(pcm & 0xFF);
      buf[o + 1] = (byte)((pcm >> 8) & 0xFF);
      return o + 2;
    }
  }

  // ==========================================================================

  /// <summary>
  /// One WASAPI endpoint, converted to 48 kHz stereo float and pushed into a
  /// ring buffer.
  ///
  /// The device's own mix format is used rather than asking WASAPI to convert:
  /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM is unreliable in combination with
  /// loopback across driver vendors, and doing it here is a few dozen lines
  /// that behave identically everywhere.
  /// </summary>
  internal class WasapiCapture : IDisposable {

    /// <summary>Ring capacity. A second is far more than the ~15 ms the pacer
    /// is ever behind, and bounds memory at 384 KB.</summary>
    const int RingFrames = AudioEngine.Rate;

    /// <summary>Shared-mode buffer we ask WASAPI for, in 100 ns units.</summary>
    const long BufferDuration = 2000000;              // 200 ms

    IMMDeviceEnumerator _enumerator;
    IMMDevice _device;
    IAudioClient _client;
    IAudioCaptureClient _capture;

    Thread _thread;
    volatile bool _running;

    readonly bool _loopback;
    readonly string _label;

    // Source format
    int _srcChannels, _srcRate, _srcBits;
    bool _srcFloat;
    int _srcBlockAlign;

    // Resampler state, one carry sample per channel (cubic needs the previous
    // three, kept as a small history).
    double _resamplePos;
    readonly float[] _histL = new float[4];
    readonly float[] _histR = new float[4];
    bool _histPrimed;

    // Ring
    readonly float[] _ring = new float[RingFrames * AudioEngine.Channels];
    int _readPos, _writePos, _count;
    readonly object _ringGate = new object();

    WasapiCapture(bool loopback, string label) {
      _loopback = loopback;
      _label = label;
    }

    // ------------------------------------------------------------- opening

    public static WasapiCapture OpenLoopback() {
      var c = new WasapiCapture(true, "system audio");
      c.OpenAndRun(null, EDataFlow.eRender);
      return c;
    }

    public static WasapiCapture OpenMicrophone(string deviceId) {
      var c = new WasapiCapture(false, "microphone");
      c.OpenAndRun(deviceId, EDataFlow.eCapture);
      return c;
    }

    /// <summary>
    /// Start the capture thread and wait for it to report whether the device
    /// opened.
    ///
    /// The COM objects are created on that thread rather than on the caller's,
    /// and every call on them happens there too. Creating them on the UI thread
    /// - which is an STA - and then using them from an MTA worker means every
    /// GetBuffer either goes through an apartment proxy back to the UI thread
    /// or works only because the object happened to be free-threaded. Neither
    /// is something to rely on a hundred times a second for hours.
    /// </summary>
    void OpenAndRun(string deviceId, EDataFlow flow) {
      var opened = new ManualResetEvent(false);
      string error = null;

      _running = true;
      _thread = new Thread(delegate () {
        try {
          Open(deviceId, flow);
        } catch (Exception err) {
          error = err.Message;
          ReleaseCom();
          opened.Set();
          return;
        }
        opened.Set();
        try { Loop(); } finally { ReleaseCom(); }
      });
      _thread.IsBackground = true;
      _thread.Name = "wasapi-" + (_loopback ? "loopback" : "mic");
      _thread.SetApartmentState(ApartmentState.MTA);
      _thread.Start();

      if (!opened.WaitOne(6000)) {
        _running = false;
        throw new Exception(_label + " timed out while opening");
      }
      if (error != null) {
        _running = false;
        throw new Exception(error);
      }
    }

    void Open(string deviceId, EDataFlow flow) {
      _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

      int hr;
      if (!string.IsNullOrEmpty(deviceId) && deviceId != "default") {
        hr = _enumerator.GetDevice(deviceId, out _device);
        // A microphone that has been unplugged since it was chosen should not
        // stop the recording; fall back to whatever is default now.
        if (!Com.Ok(hr) || _device == null)
          hr = _enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out _device);
      } else {
        // The console role is what Windows calls the Default Device, and what
        // browsers and other recorders open. The communications default can
        // be a different microphone altogether - a headset's hands-free input,
        // say, which is narrowband and sounds muffled next to the real thing.
        hr = _enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out _device);
      }
      if (!Com.Ok(hr) || _device == null) throw new Exception("no " + _label + " device");

      object obj;
      Guid iid = Com.IID_IAudioClient;
      hr = _device.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out obj);
      if (!Com.Ok(hr) || obj == null) throw new Exception("could not open " + _label);
      _client = (IAudioClient)obj;

      IntPtr pFormat;
      hr = _client.GetMixFormat(out pFormat);
      if (!Com.Ok(hr) || pFormat == IntPtr.Zero) throw new Exception("no mix format for " + _label);

      try {
        ReadFormat(pFormat);

        uint flags = AudClnt.STREAMFLAGS_NOPERSIST;
        if (_loopback) flags |= AudClnt.STREAMFLAGS_LOOPBACK;

        hr = _client.Initialize(AudClnt.SHAREMODE_SHARED, flags, BufferDuration, 0, pFormat, IntPtr.Zero);
        if (!Com.Ok(hr)) throw new Exception("initialise failed (0x" + hr.ToString("x8") + ")");
      } finally {
        Com.CoTaskMemFree(pFormat);
      }

      Guid capIid = Com.IID_IAudioCaptureClient;
      hr = _client.GetService(ref capIid, out obj);
      if (!Com.Ok(hr) || obj == null) throw new Exception("no capture service for " + _label);
      _capture = (IAudioCaptureClient)obj;
    }

    void ReadFormat(IntPtr p) {
      var wf = (WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(p, typeof(WAVEFORMATEXTENSIBLE));
      _srcChannels = wf.nChannels < 1 ? 1 : wf.nChannels;
      _srcRate = (int)wf.nSamplesPerSec;
      _srcBits = wf.wBitsPerSample;
      _srcBlockAlign = wf.nBlockAlign;

      if (wf.wFormatTag == WaveFormat.IEEE_FLOAT) _srcFloat = true;
      else if (wf.wFormatTag == WaveFormat.PCM) _srcFloat = false;
      else if (wf.wFormatTag == WaveFormat.EXTENSIBLE && wf.cbSize >= 22)
        _srcFloat = wf.SubFormat == WaveFormat.SUBTYPE_IEEE_FLOAT;
      else
        _srcFloat = _srcBits == 32;                    // the only sane guess left

      if (_srcBlockAlign <= 0) _srcBlockAlign = _srcChannels * (_srcBits / 8);
      if (_srcRate <= 0) _srcRate = AudioEngine.Rate;
    }

    // ------------------------------------------------------------- running

    void Loop() {
      try {
        int hr = _client.Start();
        if (!Com.Ok(hr)) { Log.Warn("audio: " + _label + " start failed 0x" + hr.ToString("x8")); return; }
      } catch (Exception err) {
        Log.Warn("audio: " + _label + " start threw: " + err.Message);
        return;
      }

      // Polled rather than event-driven on purpose. Loopback never signals its
      // event while nothing is playing, so an event loop would sit blocked
      // exactly when the pacer most needs to know the source is dry.
      while (_running) {
        try {
          uint packet;
          if (!Com.Ok(_capture.GetNextPacketSize(out packet))) break;

          if (packet == 0) { Thread.Sleep(8); continue; }

          while (packet > 0 && _running) {
            IntPtr data;
            uint frames, flags;
            ulong devPos, qpc;

            int hr = _capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc);
            if (hr == AudClnt.S_FALSE) break;
            if (!Com.Ok(hr)) return;

            bool silent = (flags & AudClnt.BUFFERFLAGS_SILENT) != 0;
            if (frames > 0) Convert(data, (int)frames, silent);

            _capture.ReleaseBuffer(frames);
            if (!Com.Ok(_capture.GetNextPacketSize(out packet))) break;
          }
        } catch (Exception err) {
          Log.Warn("audio: " + _label + " capture stopped: " + err.Message);
          return;
        }
      }

      try { _client.Stop(); } catch (Exception) { }
    }

    // ---------------------------------------------------------- conversion

    /// <summary>
    /// Whatever the device produced becomes 48 kHz interleaved stereo float in
    /// the ring. Scratch buffers are grown once and reused; this runs a hundred
    /// times a second for hours.
    /// </summary>
    float[] _scratchL, _scratchR;

    void Convert(IntPtr data, int frames, bool silent) {
      if (_scratchL == null || _scratchL.Length < frames) {
        _scratchL = new float[frames + 1024];
        _scratchR = new float[frames + 1024];
      }

      if (silent) {
        Array.Clear(_scratchL, 0, frames);
        Array.Clear(_scratchR, 0, frames);
      } else {
        Deinterleave(data, frames);
      }

      if (_srcRate == AudioEngine.Rate) PushDirect(frames);
      else PushResampled(frames);
    }

    unsafe void Deinterleave(IntPtr data, int frames) {
      byte* p = (byte*)data;
      int stride = _srcBlockAlign;
      int ch = _srcChannels;
      int bytes = _srcBits / 8;

      for (int f = 0; f < frames; f++) {
        byte* frame = p + (long)f * stride;
        float l = Sample(frame, 0, bytes);
        float r = ch > 1 ? Sample(frame, bytes, bytes) : l;
        _scratchL[f] = l;
        _scratchR[f] = r;
      }
    }

    unsafe float Sample(byte* frame, int offset, int bytes) {
      byte* q = frame + offset;
      if (_srcFloat) {
        if (bytes == 4) return *(float*)q;
        if (bytes == 8) return (float)(*(double*)q);
        return 0f;
      }
      switch (bytes) {
        case 2: return *(short*)q * (1f / 32768f);
        case 4: return *(int*)q * (1f / 2147483648f);
        case 3: {
          int v = q[0] | (q[1] << 8) | ((sbyte)q[2] << 16);
          return v * (1f / 8388608f);
        }
        case 1: return (q[0] - 128) * (1f / 128f);
      }
      return 0f;
    }

    void PushDirect(int frames) {
      lock (_ringGate) {
        for (int f = 0; f < frames; f++) WriteFrame(_scratchL[f], _scratchR[f]);
      }
    }

    /// <summary>
    /// Catmull-Rom resample onto the engine rate. Nearly every endpoint already
    /// runs at 48 kHz so this is usually dead code, but a 44.1 kHz microphone
    /// is common enough that linear interpolation's whine is worth avoiding.
    /// </summary>
    void PushResampled(int frames) {
      double ratio = (double)_srcRate / AudioEngine.Rate;

      lock (_ringGate) {
        for (int f = 0; f < frames; f++) {
          // Slide the four-sample history along.
          _histL[0] = _histL[1]; _histL[1] = _histL[2]; _histL[2] = _histL[3]; _histL[3] = _scratchL[f];
          _histR[0] = _histR[1]; _histR[1] = _histR[2]; _histR[2] = _histR[3]; _histR[3] = _scratchR[f];

          if (!_histPrimed) {
            // Prime with the first sample so the stream does not open with a
            // click from three zeroes.
            _histL[0] = _histL[1] = _histL[2] = _scratchL[f];
            _histR[0] = _histR[1] = _histR[2] = _scratchR[f];
            _histPrimed = true;
            _resamplePos = 1.0;
          }

          _resamplePos -= 1.0;
          while (_resamplePos < 1.0) {
            float t = (float)_resamplePos;
            WriteFrame(Cubic(_histL, t), Cubic(_histR, t));
            _resamplePos += ratio;
          }
        }
      }
    }

    static float Cubic(float[] h, float t) {
      float a = h[0], b = h[1], c = h[2], d = h[3];
      float c0 = b;
      float c1 = 0.5f * (c - a);
      float c2 = a - 2.5f * b + 2f * c - 0.5f * d;
      float c3 = 0.5f * (d - a) + 1.5f * (b - c);
      return ((c3 * t + c2) * t + c1) * t + c0;
    }

    /// <summary>Caller holds _ringGate.</summary>
    void WriteFrame(float l, float r) {
      if (_count >= RingFrames) {
        // Overrun: the pacer has stalled. Drop the oldest frame rather than
        // grow, so latency stays bounded no matter what the rest of the system
        // is doing.
        _readPos = (_readPos + 1) % RingFrames;
        _count--;
      }
      int i = _writePos * AudioEngine.Channels;
      _ring[i] = l;
      _ring[i + 1] = r;
      _writePos = (_writePos + 1) % RingFrames;
      _count++;
    }

    /// <summary>Throw away everything buffered so far.</summary>
    public void Flush() {
      lock (_ringGate) { _readPos = 0; _writePos = 0; _count = 0; }
    }

    /// <summary>
    /// Fill <paramref name="samples"/> interleaved stereo samples, zero-padding
    /// whatever the source has not produced. Silence rather than a short read
    /// is the whole point: the timeline must advance even when nothing plays.
    /// </summary>
    public void Read(float[] dest, int samples) {
      int frames = samples / AudioEngine.Channels;
      int o = 0;
      lock (_ringGate) {
        int take = frames < _count ? frames : _count;
        for (int f = 0; f < take; f++) {
          int i = _readPos * AudioEngine.Channels;
          dest[o++] = _ring[i];
          dest[o++] = _ring[i + 1];
          _readPos = (_readPos + 1) % RingFrames;
          _count--;
        }
      }
      while (o < samples) dest[o++] = 0f;
    }

    // ------------------------------------------------------------- disposal

    public void Dispose() {
      _running = false;
      Thread t = _thread;
      _thread = null;
      // The capture thread releases the COM objects itself as it unwinds, for
      // the same apartment reason it created them.
      if (t != null) { try { t.Join(1500); } catch (Exception) { } }
    }

    void ReleaseCom() {
      Com.Release(_capture); _capture = null;
      Com.Release(_client); _client = null;
      Com.Release(_device); _device = null;
      Com.Release(_enumerator); _enumerator = null;
    }
  }

  // ==========================================================================

  internal class AudioDeviceInfo {
    public string Id;
    public string Name;
  }

  /// <summary>Microphone enumeration for the Settings window. Opened and
  /// released inside the call, so nothing stays resident for it.</summary>
  internal static class AudioDevices {

    public static List<AudioDeviceInfo> ListMicrophones() {
      var list = new List<AudioDeviceInfo>();
      IMMDeviceEnumerator en = null;
      IMMDeviceCollection col = null;
      try {
        en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (!Com.Ok(en.EnumAudioEndpoints(EDataFlow.eCapture, DeviceState.Active, out col)) || col == null)
          return list;

        uint count;
        if (!Com.Ok(col.GetCount(out count))) return list;

        for (uint i = 0; i < count; i++) {
          IMMDevice dev = null;
          try {
            if (!Com.Ok(col.Item(i, out dev)) || dev == null) continue;
            string id;
            if (!Com.Ok(dev.GetId(out id)) || string.IsNullOrEmpty(id)) continue;

            var info = new AudioDeviceInfo();
            info.Id = id;
            info.Name = FriendlyName(dev) ?? "Microphone";
            list.Add(info);
          } catch (Exception) {
          } finally {
            Com.Release(dev);
          }
        }
      } catch (Exception err) {
        Log.Warn("could not list microphones: " + err.Message);
      } finally {
        Com.Release(col);
        Com.Release(en);
      }
      return list;
    }

    static string FriendlyName(IMMDevice dev) {
      IPropertyStore store = null;
      try {
        if (!Com.Ok(dev.OpenPropertyStore(Com.STGM_READ, out store)) || store == null) return null;
        PROPERTYKEY key = Com.PKEY_Device_FriendlyName;
        PROPVARIANT pv;
        if (!Com.Ok(store.GetValue(ref key, out pv))) return null;
        try {
          return pv.pointerValue != IntPtr.Zero ? Marshal.PtrToStringUni(pv.pointerValue) : null;
        } finally {
          Com.PropVariantClear(ref pv);
        }
      } catch (Exception) {
        return null;
      } finally {
        Com.Release(store);
      }
    }
  }
}
