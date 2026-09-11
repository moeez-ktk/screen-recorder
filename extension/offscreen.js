/**
 * Encodes one browser tab and streams it to the desktop app.
 *
 * This exists because Windows cannot isolate a single tab's audio - all tabs
 * share the browser's audio process. chrome.tabCapture can, so a tab
 * recording made here contains that tab and nothing else: another tab playing
 * music simply is not in the stream.
 *
 * Chunks go out over a dedicated WebSocket as soon as MediaRecorder produces
 * them, so nothing accumulates in memory no matter how long the recording is.
 */

const TIMESLICE_MS = 2000;

let recorder = null;
let stream = null;
let socket = null;
let monitorCtx = null;

function toBackground(msg) {
  chrome.runtime.sendMessage(Object.assign({ target: 'background' }, msg));
}

/** Prefer MP4 so the saved file needs no conversion; fall back to WebM. */
function pickMime() {
  const candidates = [
    'video/mp4;codecs=avc1.42E01E,mp4a.40.2',
    'video/mp4;codecs=avc1,mp4a.40.2',
    'video/mp4',
    'video/webm;codecs=vp9,opus',
    'video/webm;codecs=vp8,opus',
    'video/webm'
  ];
  for (const m of candidates) {
    if (MediaRecorder.isTypeSupported(m)) return m;
  }
  return '';
}

function openSocket(port, token) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    ws.binaryType = 'arraybuffer';
    const timer = setTimeout(() => reject(new Error('bridge connection timed out')), 5000);

    ws.onopen = () => ws.send(JSON.stringify({ type: 'hello', token, role: 'data' }));
    ws.onmessage = e => {
      if (typeof e.data !== 'string') return;
      try {
        if (JSON.parse(e.data).type === 'welcome') { clearTimeout(timer); resolve(ws); }
      } catch (_) { /* ignore */ }
    };
    ws.onerror = () => { clearTimeout(timer); reject(new Error('could not reach the recorder')); };
    ws.onclose = () => { if (recorder && recorder.state !== 'inactive') stop(); };
  });
}

async function start({ streamId, opts, port, token }) {
  try {
    socket = await openSocket(port, token);

    const video = {
      mandatory: {
        chromeMediaSource: 'tab',
        chromeMediaSourceId: streamId,
        maxFrameRate: Number(opts.fps) || 60
      }
    };
    // Cap the captured resolution when the user asked for a smaller output;
    // capturing small is far cheaper than capturing big and scaling.
    const target = parseInt(opts.resolution, 10);
    if (target) {
      video.mandatory.maxHeight = target;
      video.mandatory.maxWidth = Math.round(target * 16 / 9);
    }

    stream = await navigator.mediaDevices.getUserMedia({
      video,
      audio: opts.tabAudioEnabled === false ? false : {
        mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: streamId }
      }
    });

    // tabCapture removes the audio from the speakers; route it back so the
    // user still hears what they are recording.
    if (stream.getAudioTracks().length) {
      monitorCtx = new AudioContext();
      const src = monitorCtx.createMediaStreamSource(stream);
      src.connect(monitorCtx.destination);
    }

    // Optional voice-over. Silently skipped if the extension has no mic
    // permission - the popup offers a button to grant it.
    if (opts.micEnabled) {
      try {
        const mic = await navigator.mediaDevices.getUserMedia({ audio: true });
        const mixCtx = monitorCtx || new AudioContext();
        const dest = mixCtx.createMediaStreamDestination();
        mixCtx.createMediaStreamSource(stream).connect(dest);
        mixCtx.createMediaStreamSource(mic).connect(dest);
        for (const t of stream.getAudioTracks()) stream.removeTrack(t);
        for (const t of dest.stream.getAudioTracks()) stream.addTrack(t);
      } catch (_) { /* no mic permission; record tab audio only */ }
    }

    const mimeType = pickMime();
    const bitsPerSecond = Math.max(1_000_000, (Number(opts.bitrateMbps) || 12) * 1_000_000);
    recorder = new MediaRecorder(stream, {
      mimeType,
      videoBitsPerSecond: bitsPerSecond,
      audioBitsPerSecond: 160_000
    });

    recorder.ondataavailable = async e => {
      if (!e.data || !e.data.size) return;
      if (!socket || socket.readyState !== WebSocket.OPEN) return;
      // Backpressure: if the socket is congested, skip rather than queue -
      // unbounded buffering is what turns a long recording into an OOM.
      if (socket.bufferedAmount > 32 * 1024 * 1024) return;
      socket.send(await e.data.arrayBuffer());
    };

    recorder.onerror = ev => {
      toBackground({ type: 'error', error: (ev.error && ev.error.message) || 'recorder failed' });
      cleanup();
    };

    recorder.onstop = () => {
      toBackground({ type: 'stopped' });
      cleanup();
    };

    // If the user closes the tab, end cleanly instead of hanging.
    for (const t of stream.getTracks()) {
      t.addEventListener('ended', () => stop());
    }

    recorder.start(TIMESLICE_MS);
    toBackground({ type: 'started', mime: mimeType });
  } catch (err) {
    toBackground({ type: 'error', error: err.message });
    cleanup();
  }
}

function stop() {
  try {
    if (recorder && recorder.state !== 'inactive') recorder.stop();
    else cleanup();
  } catch (_) { cleanup(); }
}

function cleanup() {
  try { if (stream) for (const t of stream.getTracks()) t.stop(); } catch (_) {}
  try { if (monitorCtx) monitorCtx.close(); } catch (_) {}
  // Give the final chunk a moment to flush before dropping the socket.
  const s = socket;
  setTimeout(() => { try { if (s) s.close(); } catch (_) {} }, 600);
  recorder = null;
  stream = null;
  socket = null;
  monitorCtx = null;
}

chrome.runtime.onMessage.addListener(msg => {
  if (!msg || msg.target !== 'offscreen') return;
  if (msg.type === 'start') start(msg);
  else if (msg.type === 'stop') stop();
  // MediaRecorder leaves paused time out of its timestamps, so the file
  // simply carries on where it left off.
  else if (msg.type === 'pause') { if (recorder && recorder.state === 'recording') recorder.pause(); }
  else if (msg.type === 'resume') { if (recorder && recorder.state === 'paused') recorder.resume(); }
});
