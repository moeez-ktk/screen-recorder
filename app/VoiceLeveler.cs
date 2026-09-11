using System;

namespace LightRecorder {

  /// <summary>
  /// Automatic level for the microphone.
  ///
  /// Raw WASAPI capture is exactly as loud as the microphone is, and most
  /// microphones are quiet: speech typically lands around -35 to -30 dBFS,
  /// far below the system audio it is mixed with. Browser-based recorders
  /// never showed this because Chromium runs automatic gain control on every
  /// microphone by default - which is also why the Electron build of this app
  /// sounded louder than the native one did.
  ///
  /// The same idea, kept small:
  ///
  ///   high-pass 80 Hz ─▶ level detector ─▶ gain easing toward -18 dBFS
  ///
  /// The gain only moves while someone is speaking. What counts as speaking is
  /// judged against this microphone's own noise floor, so the hiss between
  /// sentences is never pumped up to speech level. Peaks are left to the
  /// limiter at the end of the mix.
  ///
  /// Runs on the pacer thread, once per block, and allocates nothing.
  /// </summary>
  internal sealed class VoiceLeveler {

    /// <summary>Where speech settles, as a short-term RMS level.</summary>
    const double TargetRms = 0.126;                  // -18 dBFS
    const double MaxGainDb = 20, MinGainDb = -6;

    /// <summary>Up slowly so it follows the speaker rather than each
    /// syllable; down faster so a sudden shout is reined in.</summary>
    const double RiseDbPerSecond = 6, FallDbPerSecond = 20;

    /// <summary>Where the gain starts before it has heard anyone. A plain
    /// microphone always needs lifting, and starting at unity would make the
    /// opening seconds of the first recording noticeably quiet.</summary>
    const double InitialGainDb = 8;

    const double DetectorSeconds = 0.3;
    const double HighPassHz = 80;

    /// <summary>Speech is anything this far above the noise floor...</summary>
    const double GateRatio = 3.162;                  // +10 dB
    /// <summary>...and above this, whatever the floor says.</summary>
    const double AbsoluteGate = 0.001;               // -60 dBFS

    /// <summary>The floor is the quietest the signal has been over the last
    /// one to two windows, so it follows a fan spinning up within seconds and
    /// does not mistake a long sentence for a louder room for long.</summary>
    const double FloorWindowSeconds = 1.5;

    /// <summary>
    /// Loud is not the same as speech. Typing, a mouse on the desk and hiss
    /// are all loud enough to pass the gate, and lifting them to speaking
    /// level is the worst thing an AGC can do. What sets voice apart is that
    /// most of its energy is voiced - a pitch and its harmonics, all under a
    /// few kHz - so it crosses zero rarely: a few percent of samples, where
    /// clicks and hiss cross on a third to a half of them. The gain only
    /// moves while the signal, smoothed over a tenth of a second, is voiced.
    /// </summary>
    const double VoicedZeroCrossingRate = 0.12;
    const double VoicingSeconds = 0.1;

    readonly int _rate;
    readonly int _floorWindowFrames;
    readonly double _b0, _b1, _b2, _a1, _a2;
    double _lx1, _lx2, _ly1, _ly2, _rx1, _rx2, _ry1, _ry2;

    double _meanSquare;
    double _windowMin, _previousMin;
    int _windowFrames;
    bool _primed;
    double _zeroCrossingRate;
    bool _lastPositive;

    double _gainDb = InitialGainDb;
    double _appliedGain = Math.Pow(10, InitialGainDb / 20);

    public VoiceLeveler(int rate) {
      _rate = rate;
      _floorWindowFrames = (int)(FloorWindowSeconds * rate);

      // RBJ cookbook high-pass at Butterworth Q. Rumble, desk thumps and DC
      // carry a lot of energy nobody hears; left in, they would both fool the
      // detector and spend headroom the gain wants.
      double w0 = 2 * Math.PI * HighPassHz / rate;
      double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
      double a0 = 1 + alpha;
      _b0 = (1 + cos) / 2 / a0;
      _b1 = -(1 + cos) / a0;
      _b2 = (1 + cos) / 2 / a0;
      _a1 = -2 * cos / a0;
      _a2 = (1 - alpha) / a0;
    }

    public double GainDb { get { return _gainDb; } }

    /// <summary>Forget the signal but keep the learned gain: the next
    /// recording is most likely the same voice on the same microphone.</summary>
    public void Reset() {
      _lx1 = _lx2 = _ly1 = _ly2 = _rx1 = _rx2 = _ry1 = _ry2 = 0;
      _meanSquare = 0;
      _windowFrames = 0;
      _primed = false;
      _appliedGain = Math.Pow(10, _gainDb / 20);
    }

    /// <summary>Level <paramref name="frames"/> interleaved stereo frames in
    /// place.</summary>
    public void Process(float[] buf, int frames) {
      if (frames <= 0) return;
      int samples = frames * 2;

      // High-pass, measuring the energy of what is left and how often it
      // changes sign.
      double energy = 0;
      int crossings = 0;
      bool positive = _lastPositive;
      for (int i = 0; i < samples; i += 2) {
        double xl = buf[i];
        double yl = _b0 * xl + _b1 * _lx1 + _b2 * _lx2 - _a1 * _ly1 - _a2 * _ly2;
        // Flushed well below hearing: a decaying filter state otherwise sinks
        // into denormal numbers, where every multiply is a hundred times slower.
        if (yl > -1e-15 && yl < 1e-15) yl = 0;
        _lx2 = _lx1; _lx1 = xl; _ly2 = _ly1; _ly1 = yl;

        double xr = buf[i + 1];
        double yr = _b0 * xr + _b1 * _rx1 + _b2 * _rx2 - _a1 * _ry1 - _a2 * _ry2;
        if (yr > -1e-15 && yr < 1e-15) yr = 0;
        _rx2 = _rx1; _rx1 = xr; _ry2 = _ry1; _ry1 = yr;

        buf[i] = (float)yl;
        buf[i + 1] = (float)yr;
        energy += yl * yl + yr * yr;
        if ((yl >= 0) != positive) { crossings++; positive = !positive; }
      }
      _lastPositive = positive;
      double blockMeanSquare = energy / samples;

      // Digital silence - a muted device, or nothing captured yet - says
      // nothing about the room or the speaker, so it moves neither the
      // detector, the floor nor the gain. Otherwise a mute switch would drag
      // the floor down to nothing, and for the seconds it took to find the
      // room again after unmuting, plain hiss would read as speech and be
      // lifted with it.
      if (blockMeanSquare > 1e-10) Measure(blockMeanSquare, (double)crossings / frames, frames);

      // Glide across the block so a gain change is never a step.
      double target = Math.Pow(10, _gainDb / 20);
      float g = (float)_appliedGain;
      float dg = (float)((target - _appliedGain) / frames);
      for (int i = 0; i < samples; i += 2) {
        g += dg;
        buf[i] *= g;
        buf[i + 1] *= g;
      }
      _appliedGain = target;
    }

    void Measure(double blockMeanSquare, double zeroCrossingRate, int frames) {
      if (!_primed) {
        // Start the detector at the first real level rather than ramping up
        // from zero, or the floor would begin far below the room and the
        // first seconds of hiss would read as speech.
        _meanSquare = blockMeanSquare;
        _windowMin = _previousMin = Math.Sqrt(blockMeanSquare);
        _zeroCrossingRate = zeroCrossingRate;
        _primed = true;
      } else {
        double a = 1 - Math.Exp(-frames / (DetectorSeconds * _rate));
        _meanSquare += a * (blockMeanSquare - _meanSquare);
        double v = 1 - Math.Exp(-frames / (VoicingSeconds * _rate));
        _zeroCrossingRate += v * (zeroCrossingRate - _zeroCrossingRate);
      }
      double level = Math.Sqrt(_meanSquare);

      if (level < _windowMin) _windowMin = level;
      _windowFrames += frames;
      if (_windowFrames >= _floorWindowFrames) {
        _previousMin = _windowMin;
        _windowMin = level;
        _windowFrames = 0;
      }
      double floor = Math.Min(_windowMin, _previousMin);

      bool speaking = level > AbsoluteGate && level > floor * GateRatio &&
                      _zeroCrossingRate < VoicedZeroCrossingRate;
      if (speaking) {
        double desired = 20 * Math.Log10(TargetRms / level);
        if (desired > MaxGainDb) desired = MaxGainDb;
        else if (desired < MinGainDb) desired = MinGainDb;

        double seconds = (double)frames / _rate;
        if (desired > _gainDb) _gainDb = Math.Min(desired, _gainDb + RiseDbPerSecond * seconds);
        else _gainDb = Math.Max(desired, _gainDb - FallDbPerSecond * seconds);
      }
    }
  }

  /// <summary>
  /// A peak limiter with instant attack and a smooth release.
  ///
  /// No lookahead, so the gain drops on the very sample that would have gone
  /// over. That is a touch less transparent than a mastering limiter, but it
  /// never lets a sample past the ceiling, costs a compare and a multiply, and
  /// adds no latency to a stream whose timing is the whole point.
  /// </summary>
  internal sealed class PeakLimiter {

    readonly float _ceiling, _release;
    float _envelope;

    public PeakLimiter(int rate, float ceiling, double releaseSeconds) {
      _ceiling = ceiling;
      _release = (float)Math.Exp(-1.0 / (releaseSeconds * rate));
    }

    public void Reset() { _envelope = 0f; }

    public void Process(ref float l, ref float r) {
      float peak = Math.Max(Math.Abs(l), Math.Abs(r));
      float env = _envelope * _release;
      if (peak > env) env = peak;
      // Nowhere near the ceiling, and it keeps the decay out of denormals.
      if (env < 1e-6f) env = 0f;
      _envelope = env;

      if (env > _ceiling) {
        float g = _ceiling / env;
        l *= g;
        r *= g;
      }
    }
  }
}
