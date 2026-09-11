'use strict';
/**
 * The recording engine.
 *
 * Design rule that makes long recordings cheap: **no video frame ever enters
 * this process**. ffmpeg captures via the Desktop Duplication API (GPU),
 * encodes on the GPU, and muxes straight to disk. We only ever hold a few KB
 * of progress text and a small ring of PCM audio. Memory is therefore flat
 * whether the recording lasts 10 seconds or 10 hours.
 *
 * Audio arrives from the hidden mixer renderer as 48 kHz s16le stereo and is
 * fed to ffmpeg over a Windows named pipe. That leaves ffmpeg's stdin free so
 * we can send 'q' for a clean, fully-flushed shutdown.
 */
const { EventEmitter } = require('events');
const { spawn } = require('child_process');
const net = require('net');
const fs = require('fs');
const path = require('path');

const AUDIO_RATE = 48000;
const AUDIO_CHANNELS = 2;
/** Hard cap on queued audio if the pipe stalls. ~1s of PCM; beyond this we drop. */
const AUDIO_QUEUE_LIMIT = AUDIO_RATE * AUDIO_CHANNELS * 2;

let pipeSeq = 0;

class Recorder extends EventEmitter {
  constructor() {
    super();
    this.state = 'idle';           // idle | starting | recording | stopping
    this.proc = null;
    this.pipeServer = null;
    this.audioSocket = null;
    this.audioQueue = [];
    this.audioQueued = 0;
    this.outFile = null;
    this.startedAt = 0;
    this.lastProgress = null;
    this.stderrTail = [];
    this._stopTimer = null;
    this._exitResolve = null;
  }

  get isRecording() {
    return this.state === 'recording' || this.state === 'starting';
  }

  // ---------------------------------------------------------------- filenames

  static buildFilename(pattern, container) {
    const d = new Date();
    const p2 = n => String(n).padStart(2, '0');
    const name = (pattern || 'Recording {date} {time}')
      .replace(/\{date\}/g, `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}`)
      .replace(/\{time\}/g, `${p2(d.getHours())}-${p2(d.getMinutes())}-${p2(d.getSeconds())}`)
      .replace(/\{y\}/g, String(d.getFullYear()))
      .replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
      .trim();
    return `${name || 'Recording'}.${container || 'mp4'}`;
  }

  static uniquePath(dir, filename) {
    let candidate = path.join(dir, filename);
    if (!fs.existsSync(candidate)) return candidate;
    const ext = path.extname(filename);
    const stem = filename.slice(0, -ext.length);
    for (let i = 2; i < 10000; i++) {
      candidate = path.join(dir, `${stem} (${i})${ext}`);
      if (!fs.existsSync(candidate)) return candidate;
    }
    return path.join(dir, `${stem} ${Date.now()}${ext}`);
  }

  // ------------------------------------------------------------- ffmpeg args

  /**
   * @param {object} o
   * @param {object} o.source   {kind:'screen'|'window'|'region', outputIdx, title, rect}
   * @param {object} o.settings
   * @param {string} o.encoder  concrete ffmpeg encoder id
   * @param {string|null} o.audioPipe  named pipe path, or null for silent capture
   * @param {string} o.outFile
   * @param {boolean} o.hasDdagrab
   */
  static buildArgs({ source, settings, encoder, audioPipe, outFile, hasDdagrab }) {
    const fps = Math.max(1, Math.min(240, Number(settings.fps) || 60));
    const cursor = settings.captureCursor ? 1 : 0;
    const args = ['-hide_banner', '-loglevel', 'warning', '-nostats', '-progress', 'pipe:1'];

    // Hardware encoders want NV12 (semi-planar) and genuinely fail on planar
    // yuv420p: NVENC reports "CreateInputBuffer failed: invalid param" and
    // writes nothing at all. Only libx264 gets the conventional yuv420p.
    // The filter chain ends in this format, so no global -pix_fmt is needed.
    const pixFmt = encoder === 'libx264' ? 'yuv420p' : 'nv12';

    // ---- scaling ----------------------------------------------------------
    // Always land on even dimensions; H.264 4:2:0 requires it.
    let scale;
    if (settings.resolution && settings.resolution !== 'source') {
      const h = parseInt(settings.resolution, 10);
      // Never upscale: min() keeps a 1080p monitor at 1080p even if 1440p is picked.
      scale = `scale=-2:'min(${h},ih)':flags=bicubic`;
    } else {
      scale = `scale=trunc(iw/2)*2:trunc(ih/2)*2`;
    }

    // ---- video source -----------------------------------------------------
    let videoFilter;
    let audioInputIndex;

    const useDda = hasDdagrab && (source.kind === 'screen' || source.kind === 'region');

    if (useDda) {
      // ddagrab is a filter *source*, so it consumes no -i slot. It runs on the
      // GPU via DXGI Desktop Duplication: this is what makes fullscreen game
      // capture nearly free compared to GDI.
      const opts = [
        `output_idx=${source.outputIdx || 0}`,
        `framerate=${fps}`,
        `draw_mouse=${cursor}`
      ];
      if (source.kind === 'region' && source.rect) {
        const r = source.rect;
        opts.push(`video_size=${Math.max(2, r.width & ~1)}x${Math.max(2, r.height & ~1)}`);
        opts.push(`offset_x=${r.x}`, `offset_y=${r.y}`);
      }
      args.push('-init_hw_device', 'd3d11va');
      // hwdownload is the single GPU->CPU copy; converting down from BGRA
      // afterwards halves what the encoder has to chew through.
      videoFilter = `ddagrab=${opts.join(':')},hwdownload,format=bgra,${scale},format=${pixFmt}[v]`;
      audioInputIndex = 0;
    } else {
      // Window capture (and the fallback when ddagrab is unavailable) uses GDI.
      args.push('-f', 'gdigrab', '-framerate', String(fps), '-draw_mouse', String(cursor),
                '-thread_queue_size', '512');
      if (source.kind === 'window' && source.title) {
        args.push('-i', `title=${source.title}`);
      } else if (source.kind === 'region' && source.rect) {
        const r = source.rect;
        args.push('-offset_x', String(r.x), '-offset_y', String(r.y),
                  '-video_size', `${r.width}x${r.height}`, '-i', 'desktop');
      } else {
        args.push('-i', 'desktop');
      }
      videoFilter = `[0:v]${scale},format=${pixFmt}[v]`;
      audioInputIndex = 1;
    }

    // ---- audio input ------------------------------------------------------
    if (audioPipe) {
      args.push(
        '-f', 's16le', '-ar', String(AUDIO_RATE), '-ac', String(AUDIO_CHANNELS),
        '-channel_layout', 'stereo', '-thread_queue_size', '1024',
        '-i', audioPipe
      );
    }

    args.push('-filter_complex', videoFilter, '-map', '[v]');

    if (audioPipe) {
      args.push('-map', `${audioInputIndex}:a`);
      // Guard against clock drift between the audio device and the system
      // clock. Over a multi-hour recording this is the difference between
      // perfect sync and audio sliding a second late.
      args.push('-af', 'aresample=async=1:min_hard_comp=0.100:first_pts=0');
      args.push('-c:a', 'aac', '-b:a', `${settings.audioBitrateKbps || 160}k`, '-ar', String(AUDIO_RATE));
    } else {
      args.push('-an');
    }

    // ---- video encoder ----------------------------------------------------
    const gop = String(fps * 2);
    const cq = Math.max(1, Math.min(51, Number(settings.quality) || 23));
    const byBitrate = settings.qualityMode === 'bitrate';
    const kbps = Math.max(1000, Math.round((Number(settings.bitrateMbps) || 12) * 1000));

    args.push('-c:v', encoder, '-g', gop);

    switch (encoder) {
      case 'h264_nvenc':
        args.push('-preset', 'p4', '-tune', 'hq', '-profile:v', 'high', '-bf', '2');
        if (byBitrate) {
          args.push('-rc', 'cbr', '-b:v', `${kbps}k`, '-maxrate', `${kbps}k`, '-bufsize', `${kbps * 2}k`);
        } else {
          args.push('-rc', 'vbr', '-cq', String(cq), '-b:v', '0', '-maxrate', `${kbps * 2}k`, '-bufsize', `${kbps * 4}k`);
        }
        break;

      case 'h264_qsv':
        args.push('-preset', 'medium');
        if (byBitrate) args.push('-b:v', `${kbps}k`, '-maxrate', `${kbps}k`);
        else args.push('-global_quality', String(cq), '-look_ahead', '0');
        break;

      case 'h264_amf':
        args.push('-quality', 'balanced');
        if (byBitrate) args.push('-rc', 'cbr', '-b:v', `${kbps}k`);
        else args.push('-rc', 'cqp', '-qp_i', String(cq), '-qp_p', String(cq + 2), '-qp_b', String(cq + 4));
        break;

      case 'h264_mf':
        args.push('-rate_control', byBitrate ? 'cbr' : 'quality');
        if (byBitrate) args.push('-b:v', `${kbps}k`);
        else args.push('-quality', String(Math.max(0, Math.min(100, 100 - cq * 2))));
        break;

      default: // libx264
        args.push('-preset', 'veryfast', '-profile:v', 'high');
        if (byBitrate) args.push('-b:v', `${kbps}k`, '-maxrate', `${kbps}k`, '-bufsize', `${kbps * 2}k`);
        else args.push('-crf', String(cq));
        break;
    }

    // ---- output -----------------------------------------------------------
    // Fragmented MP4: the file on disk is valid and playable at every instant,
    // so a crash or power cut costs you the last fragment, not the recording.
    args.push(
      '-movflags', '+frag_keyframe+empty_moov+default_base_moof',
      '-max_muxing_queue_size', '2048',
      '-f', 'mp4', '-y', outFile
    );

    return args;
  }

  // ------------------------------------------------------------- audio pipe

  _createAudioPipe() {
    const pipePath = `\\\\.\\pipe\\lightrec-audio-${process.pid}-${++pipeSeq}`;

    this.pipeServer = net.createServer(socket => {
      this.audioSocket = socket;
      socket.on('error', () => { /* ffmpeg exiting closes this; not an error we act on */ });
      socket.on('close', () => { this.audioSocket = null; });

      // Flush anything the mixer produced before ffmpeg connected.
      for (const chunk of this.audioQueue) socket.write(chunk);
      this.audioQueue.length = 0;
      this.audioQueued = 0;
    });

    this.pipeServer.on('error', err => this.emit('error', `Audio pipe failed: ${err.message}`));
    this.pipeServer.listen(pipePath);
    return pipePath;
  }

  /** Called from the hidden mixer renderer via IPC. `buf` is s16le stereo PCM. */
  writeAudio(buf) {
    if (!this.isRecording && this.state !== 'stopping') return;
    const chunk = Buffer.isBuffer(buf) ? buf : Buffer.from(buf);

    if (this.audioSocket && this.audioSocket.writable) {
      this.audioSocket.write(chunk);
      return;
    }
    // ffmpeg has not attached yet - buffer, but never without a ceiling.
    this.audioQueue.push(chunk);
    this.audioQueued += chunk.length;
    while (this.audioQueued > AUDIO_QUEUE_LIMIT && this.audioQueue.length) {
      this.audioQueued -= this.audioQueue.shift().length;
    }
  }

  // ------------------------------------------------------------------ start

  async start({ source, settings, encoder, ffmpeg, hasDdagrab, wantAudio }) {
    if (this.isRecording) throw new Error('Already recording');
    this.state = 'starting';
    this.stderrTail = [];
    this.lastProgress = null;

    fs.mkdirSync(settings.outputDir, { recursive: true });
    const filename = Recorder.buildFilename(settings.filenamePattern, settings.container);
    this.outFile = Recorder.uniquePath(settings.outputDir, filename);

    const audioPipe = wantAudio ? this._createAudioPipe() : null;
    const args = Recorder.buildArgs({ source, settings, encoder, audioPipe, outFile: this.outFile, hasDdagrab });

    this.proc = spawn(ffmpeg, args, {
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe']
    });

    this.startedAt = Date.now();
    this.state = 'recording';

    this._wireProgress();
    this._wireStderr();

    this.proc.on('error', err => {
      this.state = 'idle';
      this._teardownPipe();
      this.emit('error', `Could not launch ffmpeg: ${err.message}`);
      this.emit('stopped', { file: null, error: err.message });
    });

    this.proc.on('close', code => {
      const wasStopping = this.state === 'stopping';
      this.state = 'idle';
      clearTimeout(this._stopTimer);
      this._teardownPipe();

      const failed = code !== 0 && !wasStopping;
      const result = {
        file: this.outFile,
        durationMs: Date.now() - this.startedAt,
        error: failed ? (this.stderrTail.join('\n') || `ffmpeg exited with code ${code}`) : null
      };
      this.emit('stopped', result);
      if (this._exitResolve) { this._exitResolve(result); this._exitResolve = null; }
    });

    this.emit('started', { file: this.outFile, args });
    return { file: this.outFile, args };
  }

  _wireProgress() {
    // -progress pipe:1 emits key=value lines; far cheaper to parse than
    // scraping the human-readable stats block off stderr.
    let buf = '';
    this.proc.stdout.setEncoding('utf8');
    this.proc.stdout.on('data', text => {
      buf += text;
      const lines = buf.split('\n');
      buf = lines.pop();
      const p = {};
      for (const line of lines) {
        const i = line.indexOf('=');
        if (i > 0) p[line.slice(0, i).trim()] = line.slice(i + 1).trim();
      }
      if (Object.keys(p).length) {
        this.lastProgress = {
          frames: Number(p.frame) || 0,
          fps: Number(p.fps) || 0,
          bytes: Number(p.total_size) || 0,
          timeMs: Math.floor((Number(p.out_time_us) || Number(p.out_time_ms) * 1000 || 0) / 1000),
          dropped: Number(p.drop_frames) || 0,
          speed: p.speed || ''
        };
        this.emit('progress', this.lastProgress);
      }
    });
  }

  _wireStderr() {
    this.proc.stderr.setEncoding('utf8');
    this.proc.stderr.on('data', text => {
      for (const line of text.split(/\r?\n/)) {
        if (!line.trim()) continue;
        this.stderrTail.push(line);
        if (this.stderrTail.length > 40) this.stderrTail.shift();  // bounded: never grows
      }
    });
  }

  // ------------------------------------------------------------------- stop

  stop() {
    if (!this.proc || this.state === 'idle') return Promise.resolve(null);
    if (this.state === 'stopping') {
      return new Promise(res => { this._exitResolve = res; });
    }
    this.state = 'stopping';

    const done = new Promise(res => { this._exitResolve = res; });

    // 'q' makes ffmpeg flush the encoder and finalise the mp4 properly.
    try {
      this.proc.stdin.write('q');
      this.proc.stdin.end();
    } catch (_) { /* already gone */ }

    // Closing the audio pipe lets ffmpeg see EOF on that input.
    try { if (this.audioSocket) this.audioSocket.end(); } catch (_) {}

    // If it will not go quietly, kill it. Fragmented MP4 means the file on
    // disk is still playable even in that case.
    this._stopTimer = setTimeout(() => {
      if (this.proc && this.state === 'stopping') {
        try { this.proc.kill('SIGKILL'); } catch (_) {}
      }
    }, 8000);

    return done;
  }

  _teardownPipe() {
    try { if (this.audioSocket) this.audioSocket.destroy(); } catch (_) {}
    try { if (this.pipeServer) this.pipeServer.close(); } catch (_) {}
    this.audioSocket = null;
    this.pipeServer = null;
    this.audioQueue.length = 0;
    this.audioQueued = 0;
    this.proc = null;
  }

  status() {
    return {
      state: this.state,
      file: this.outFile,
      startedAt: this.startedAt,
      elapsedMs: this.isRecording ? Date.now() - this.startedAt : 0,
      progress: this.lastProgress
    };
  }
}

/**
 * Optional post-pass: rewrite the fragmented MP4 into a normal one with the
 * moov atom up front. Stream copy only, so it is I/O bound rather than a
 * re-encode. Off by default because it makes stopping non-instant.
 */
function remux(ffmpeg, file) {
  return new Promise(resolve => {
    const tmp = file.replace(/\.mp4$/i, '.tmp.mp4');
    const p = spawn(ffmpeg, [
      '-hide_banner', '-loglevel', 'error', '-nostdin',
      '-i', file, '-c', 'copy', '-movflags', '+faststart', '-y', tmp
    ], { windowsHide: true });

    p.on('close', code => {
      if (code === 0 && fs.existsSync(tmp)) {
        try {
          fs.unlinkSync(file);
          fs.renameSync(tmp, file);
          return resolve({ ok: true, file });
        } catch (err) {
          return resolve({ ok: false, error: err.message });
        }
      }
      try { fs.existsSync(tmp) && fs.unlinkSync(tmp); } catch (_) {}
      resolve({ ok: false, error: `remux exited ${code}` });
    });
    p.on('error', err => resolve({ ok: false, error: err.message }));
  });
}

module.exports = { Recorder, remux, AUDIO_RATE, AUDIO_CHANNELS };
