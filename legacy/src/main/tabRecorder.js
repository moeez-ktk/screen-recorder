'use strict';
/**
 * Writes a browser-tab recording to disk.
 *
 * When the chosen source is a single tab, ffmpeg is not involved: only the
 * browser can capture one tab in isolation (and capture only that tab's
 * audio, which is the whole point). The extension encodes with MediaRecorder
 * and streams chunks over the local bridge; our only job is to append them to
 * a file as they arrive so nothing accumulates in memory.
 */
const { EventEmitter } = require('events');
const fs = require('fs');
const path = require('path');
const { Recorder } = require('./recorder');

class TabRecorder extends EventEmitter {
  constructor() {
    super();
    this.state = 'idle';
    this.stream = null;
    this.outFile = null;
    this.bytes = 0;
    this.startedAt = 0;
    this.actualExt = null;
  }

  get isRecording() {
    return this.state === 'recording' || this.state === 'starting';
  }

  start({ settings, container }) {
    if (this.isRecording) throw new Error('Already recording');
    fs.mkdirSync(settings.outputDir, { recursive: true });

    const ext = container === 'webm' ? 'webm' : 'mp4';
    const filename = Recorder.buildFilename(settings.filenamePattern, ext);
    this.outFile = Recorder.uniquePath(settings.outputDir, filename);

    this.stream = fs.createWriteStream(this.outFile);
    this.stream.on('error', err => this.emit('error', `Write failed: ${err.message}`));

    this.bytes = 0;
    this.actualExt = null;
    this.startedAt = Date.now();
    this.state = 'recording';
    this.emit('started', { file: this.outFile });
    return { file: this.outFile };
  }

  write(chunk) {
    if (!this.stream || this.state === 'idle') return;
    const buf = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    this.bytes += buf.length;
    this.stream.write(buf);
    this.emit('progress', {
      bytes: this.bytes,
      timeMs: Date.now() - this.startedAt,
      frames: 0, fps: 0, dropped: 0, speed: ''
    });
  }

  /**
   * The browser picks the container (MP4 when it can, WebM otherwise) and
   * only tells us once encoding has begun. Rather than race that, we open the
   * file straight away and correct the extension at the end.
   */
  setContainerFromMime(mime) {
    if (!mime) return;
    this.actualExt = /mp4/i.test(mime) ? 'mp4' : 'webm';
  }

  stop() {
    if (this.state === 'idle') return Promise.resolve(null);
    this.state = 'stopping';
    return new Promise(resolve => {
      const finish = () => {
        // Rename if the browser gave us WebM after we optimistically said mp4.
        if (this.actualExt && this.outFile && !this.outFile.toLowerCase().endsWith(`.${this.actualExt}`)) {
          const renamed = this.outFile.replace(/\.[^.]+$/, `.${this.actualExt}`);
          try { fs.renameSync(this.outFile, renamed); this.outFile = renamed; } catch (_) {}
        }
        const result = {
          file: this.outFile,
          durationMs: Date.now() - this.startedAt,
          bytes: this.bytes,
          error: this.bytes === 0 ? 'Nothing was captured from the tab.' : null
        };
        this.state = 'idle';
        this.stream = null;
        this.emit('stopped', result);
        resolve(result);
      };
      if (this.stream) this.stream.end(finish);
      else finish();
    });
  }

  status() {
    return {
      state: this.state,
      file: this.outFile,
      startedAt: this.startedAt,
      elapsedMs: this.isRecording ? Date.now() - this.startedAt : 0,
      progress: { bytes: this.bytes, timeMs: Date.now() - this.startedAt }
    };
  }
}

module.exports = { TabRecorder };
