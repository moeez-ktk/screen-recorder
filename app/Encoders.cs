using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LightRecorder {

  internal class EncoderInfo {
    public string Id;
    public string Label;
    public bool Hardware;

    /// <summary>
    /// True when this encoder takes D3D11 textures straight from ddagrab.
    ///
    /// This is the single most valuable thing the app knows. The old pipeline
    /// ran ddagrab,hwdownload,format=bgra,scale,format=nv12 - pulling every
    /// frame off the GPU, converting it on the CPU, and handing it back to the
    /// GPU to encode. Measured on a 20 second 1080p60 capture that cost 61
    /// seconds of CPU (about three cores saturated). Feeding the same encoder
    /// the D3D11 frames directly cost 1.0 second, for a byte-identical result.
    /// </summary>
    public bool AcceptsD3D11;
  }

  internal class EncoderSet {
    public string Ffmpeg;
    public List<EncoderInfo> Available = new List<EncoderInfo>();
    public string Best;
    public bool HasDdagrab;

    public EncoderInfo Find(string id) {
      foreach (EncoderInfo e in Available) if (e.Id == id) return e;
      return null;
    }

    /// <summary>Resolve the 'auto' setting into a concrete encoder id.</summary>
    public string Pick(string setting) {
      if (!string.IsNullOrEmpty(setting) && setting != "auto" && Find(setting) != null) return setting;
      return Best;
    }
  }

  /// <summary>
  /// Locates ffmpeg and works out what this machine can actually do.
  ///
  /// "Listed in -encoders" is not the same as "works", so each candidate is
  /// proven with a real two-frame encode before it is trusted. The result is
  /// cached on disk, so this only ever costs anything once.
  /// </summary>
  internal static class Encoders {

    /// <summary>Bumped when the cache gains a field, or when what a field
    /// proves changes, so an old cache is re-probed rather than misread.
    ///   3 - the GPU path is proven with ddagrab opened as a lavfi input, the
    ///       way recordings now open it</summary>
    const int CacheVersion = 3;

    // Best first. Hardware encoders keep the CPU free for the game or app
    // being recorded, which is the entire point.
    static readonly string[][] Candidates = {
      new string[] { "h264_nvenc", "NVIDIA NVENC (H.264)", "hw" },
      new string[] { "h264_qsv",   "Intel Quick Sync (H.264)", "hw" },
      new string[] { "h264_amf",   "AMD AMF (H.264)", "hw" },
      new string[] { "h264_mf",    "Media Foundation (H.264)", "hw" },
      new string[] { "libx264",    "libx264 (CPU)", "sw" }
    };

    static EncoderSet _cached;

    public static EncoderSet Cached { get { return _cached; } }

    // -------------------------------------------------------------- finding

    /// <summary>Explicit setting, then PATH, then a few well-known spots.</summary>
    public static string ResolveFfmpeg(string explicitPath) {
      if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

      // PATH
      try {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(';')) {
          if (string.IsNullOrEmpty(dir)) continue;
          try {
            string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) return p;
          } catch (Exception) { /* a malformed PATH entry is not our problem */ }
        }
      } catch (Exception) { }

      var common = new List<string> {
        @"C:\ffmpeg\bin",
        @"C:\Program Files\ffmpeg\bin",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims")
      };
      foreach (string dir in common) {
        try {
          string p = Path.Combine(dir, "ffmpeg.exe");
          if (File.Exists(p)) return p;
        } catch (Exception) { }
      }

      // The versioned layout ffmpeg.org ships: C:\ffmpeg\ffmpeg-<date>-full_build\bin
      try {
        if (Directory.Exists(@"C:\ffmpeg")) {
          foreach (string entry in Directory.GetDirectories(@"C:\ffmpeg")) {
            string p = Path.Combine(entry, @"bin\ffmpeg.exe");
            if (File.Exists(p)) return p;
          }
        }
      } catch (Exception) { }

      return null;
    }

    // ------------------------------------------------------------ detection

    public static EncoderSet Detect(string explicitPath, bool force) {
      if (_cached != null && !force) return _cached;

      var set = new EncoderSet();
      set.Ffmpeg = ResolveFfmpeg(explicitPath);
      if (set.Ffmpeg == null) { _cached = set; return set; }

      if (!force) {
        EncoderSet disk = LoadCache(set.Ffmpeg);
        if (disk != null) { _cached = disk; return disk; }
      }

      string encoderList = Run(set.Ffmpeg, "-hide_banner -encoders", 20000);
      string filterList = Run(set.Ffmpeg, "-hide_banner -filters", 20000);
      set.HasDdagrab = filterList.IndexOf("ddagrab", StringComparison.OrdinalIgnoreCase) >= 0;

      var declared = new HashSet<string>(StringComparer.Ordinal);
      foreach (string line in encoderList.Split('\n')) {
        string t = line.Trim();
        if (t.Length < 8) continue;
        // " V....D h264_nvenc   NVIDIA ..." - the id is the second column.
        int sp = t.IndexOf(' ');
        if (sp <= 0) continue;
        string flags = t.Substring(0, sp);
        if (flags.Length < 2 || "VAS".IndexOf(flags[0]) < 0) continue;
        string rest = t.Substring(sp).TrimStart();
        int end = rest.IndexOf(' ');
        declared.Add(end > 0 ? rest.Substring(0, end) : rest);
      }

      foreach (string[] cand in Candidates) {
        string id = cand[0];
        if (!declared.Contains(id)) continue;

        // libx264 is software and always works if it is compiled in; skip the
        // probe cost entirely.
        bool works = id == "libx264" || Probe(set.Ffmpeg, id);
        if (!works) continue;

        var info = new EncoderInfo();
        info.Id = id;
        info.Label = cand[1];
        info.Hardware = cand[2] == "hw";
        info.AcceptsD3D11 = set.HasDdagrab && SupportsD3D11(set.Ffmpeg, id) && ProbeGpuPath(set.Ffmpeg, id);
        set.Available.Add(info);
      }

      set.Best = set.Available.Count > 0 ? set.Available[0].Id : null;
      SaveCache(set);
      _cached = set;
      return set;
    }

    /// <summary>Encode two frames of colour bars to nothing. Cheap, and it
    /// fails the same way a real capture would if the encoder is missing,
    /// busy, or unlicensed.</summary>
    static bool Probe(string ffmpeg, string id) {
      string args = "-hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=30 " +
                    "-frames:v 2 -c:v " + id + " -pix_fmt yuv420p -f null -";
      return RunSucceeds(ffmpeg, args, 25000);
    }

    /// <summary>Does -h encoder=<id> list d3d11 among its input formats?</summary>
    static bool SupportsD3D11(string ffmpeg, string id) {
      string help = Run(ffmpeg, "-hide_banner -h encoder=" + id, 15000);
      foreach (string line in help.Split('\n')) {
        if (line.IndexOf("Supported pixel formats", StringComparison.OrdinalIgnoreCase) < 0) continue;
        return line.IndexOf("d3d11", StringComparison.OrdinalIgnoreCase) >= 0;
      }
      return false;
    }

    /// <summary>
    /// Prove the zero-copy path end to end rather than inferring it.
    ///
    /// Two frames of the real desktop through ddagrab, opened exactly as a
    /// recording opens it, into the real encoder. Listing d3d11 as an input
    /// format is necessary but not sufficient: the driver, the adapter ddagrab
    /// picks, and the encoder all have to agree, and finding that out here is
    /// far better than finding it out when the user presses record.
    /// </summary>
    static bool ProbeGpuPath(string ffmpeg, string id) {
      string args = "-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30 " +
                    "-frames:v 2 -c:v " + id + " -f null -";
      bool ok = RunSucceeds(ffmpeg, args, 25000);
      Log.Info("encoder " + id + ": GPU-direct path " + (ok ? "available" : "unavailable"));
      return ok;
    }

    // -------------------------------------------------------------- running

    static string Run(string exe, string args, int timeoutMs) {
      try {
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using (Process p = Process.Start(psi)) {
          var sb = new StringBuilder();
          // Both streams drained on background threads: ffmpeg writes -encoders
          // to stdout and -h to stdout, but a full pipe on either one would
          // deadlock a synchronous read.
          p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) {
            if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n');
          };
          p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) {
            if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n');
          };
          p.BeginOutputReadLine();
          p.BeginErrorReadLine();

          if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } }
          lock (sb) return sb.ToString();
        }
      } catch (Exception err) {
        Log.Warn("ffmpeg probe failed: " + err.Message);
        return "";
      }
    }

    static bool RunSucceeds(string exe, string args, int timeoutMs) {
      try {
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using (Process p = Process.Start(psi)) {
          p.OutputDataReceived += delegate { };
          p.ErrorDataReceived += delegate { };
          p.BeginOutputReadLine();
          p.BeginErrorReadLine();
          if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return false; }
          return p.ExitCode == 0;
        }
      } catch (Exception) {
        return false;
      }
    }

    // ---------------------------------------------------------------- cache

    static EncoderSet LoadCache(string ffmpeg) {
      try {
        if (!File.Exists(Paths.EncoderCache)) return null;
        var m = Json.ParseObject(File.ReadAllText(Paths.EncoderCache));
        if (Json.Int(m, "cacheVersion", 0) != CacheVersion) return null;
        if (Json.Str(m, "ffmpeg", "") != ffmpeg) return null;

        var set = new EncoderSet();
        set.Ffmpeg = ffmpeg;
        set.HasDdagrab = Json.Bool(m, "hasDdagrab", false);
        set.Best = Json.Str(m, "best", null);
        if (string.IsNullOrEmpty(set.Best)) set.Best = null;

        var arr = Json.List(m, "available");
        if (arr == null) return null;
        foreach (object o in arr) {
          var e = o as Dictionary<string, object>;
          if (e == null) continue;
          var info = new EncoderInfo();
          info.Id = Json.Str(e, "id", "");
          info.Label = Json.Str(e, "label", info.Id);
          info.Hardware = Json.Bool(e, "hw", false);
          info.AcceptsD3D11 = Json.Bool(e, "d3d11", false);
          if (!string.IsNullOrEmpty(info.Id)) set.Available.Add(info);
        }
        return set.Available.Count > 0 || set.Best == null ? set : null;
      } catch (Exception) {
        return null;
      }
    }

    static void SaveCache(EncoderSet set) {
      try {
        var m = new Dictionary<string, object>();
        m["cacheVersion"] = CacheVersion;
        m["ffmpeg"] = set.Ffmpeg;
        m["hasDdagrab"] = set.HasDdagrab;
        m["best"] = set.Best;

        var list = new List<object>();
        foreach (EncoderInfo e in set.Available) {
          var d = new Dictionary<string, object>();
          d["id"] = e.Id; d["label"] = e.Label; d["hw"] = e.Hardware; d["d3d11"] = e.AcceptsD3D11;
          list.Add(d);
        }
        m["available"] = list;

        File.WriteAllText(Paths.EncoderCache, Json.Write(m));
      } catch (Exception) {
        // The cache is an optimisation, not a requirement.
      }
    }
  }
}
