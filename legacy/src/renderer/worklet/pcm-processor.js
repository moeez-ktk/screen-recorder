/**
 * Converts the mixed float32 graph output into interleaved 16-bit stereo PCM
 * and ships it to the main thread in ~42 ms blocks.
 *
 * Runs on the audio thread, so it must stay allocation-light: one reusable
 * accumulation buffer, and the only per-block allocation is the small typed
 * array that gets transferred away (never copied).
 */
const BLOCK_FRAMES = 2048;      // 2048 / 48000 ≈ 42.7 ms
const CHANNELS = 2;

class PcmProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buf = new Int16Array(BLOCK_FRAMES * CHANNELS);
    this.used = 0;
    this.running = true;
    this.port.onmessage = e => {
      if (e.data && e.data.type === 'stop') this.running = false;
    };
  }

  process(inputs) {
    if (!this.running) return false;

    const input = inputs[0];
    if (!input || input.length === 0) {
      // No connected source yet - emit silence so the encoder timeline keeps
      // advancing rather than stalling and desyncing later.
      return true;
    }

    const left = input[0];
    const right = input.length > 1 ? input[1] : input[0];
    if (!left) return true;

    for (let i = 0; i < left.length; i++) {
      // Clamp then scale; asymmetric bounds match the int16 range exactly.
      let l = left[i];
      let r = right[i];
      l = l > 1 ? 1 : l < -1 ? -1 : l;
      r = r > 1 ? 1 : r < -1 ? -1 : r;

      this.buf[this.used++] = l < 0 ? l * 0x8000 : l * 0x7fff;
      this.buf[this.used++] = r < 0 ? r * 0x8000 : r * 0x7fff;

      if (this.used >= this.buf.length) {
        const out = this.buf.slice(0, this.used);
        this.port.postMessage(out, [out.buffer]);
        this.used = 0;
      }
    }
    return true;
  }
}

registerProcessor('pcm-processor', PcmProcessor);
