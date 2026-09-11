'use strict';
/**
 * The audio engine.
 *
 *   system loopback ──▶ gain(system) ─┐
 *                                     ├─▶ merge ─▶ pcm-processor ─▶ ffmpeg
 *   microphone      ──▶ gain(mic)  ───┘
 *
 * Both gains are live, so muting the mic or system audio takes effect
 * instantly mid-recording without touching the encoder. Everything here works
 * on ~42 ms blocks: total audio memory in flight is a few kilobytes.
 */

const RATE = 48000;

let ctx = null;
let node = null;
let systemStream = null;
let micStream = null;
let systemGain = null;
let micGain = null;
let started = false;

function report(msg) {
  try { window.audioBridge.error(msg); } catch (_) {}
}

async function getSystemAudio() {
  // Electron's display-media handler answers this automatically with
  // audio:'loopback'. We only want the audio track, so the video track is
  // stopped the moment we have it - otherwise we would be paying for a screen
  // capture we never read.
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: true,
    audio: {
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false
    }
  });
  for (const t of stream.getVideoTracks()) { t.stop(); stream.removeTrack(t); }
  if (!stream.getAudioTracks().length) throw new Error('no system audio track');
  return stream;
}

async function getMic(deviceId) {
  const constraints = {
    audio: {
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
      channelCount: 1
    },
    video: false
  };
  if (deviceId && deviceId !== 'default') {
    constraints.audio.deviceId = { exact: deviceId };
  }
  return navigator.mediaDevices.getUserMedia(constraints);
}

async function start(settings) {
  if (started) { applyConfig(settings); return; }
  started = true;

  const wantSystem = !!settings.systemAudioEnabled;
  const wantMic = !!settings.micEnabled;

  ctx = new AudioContext({ sampleRate: RATE, latencyHint: 'playback' });
  await ctx.audioWorklet.addModule('worklet/pcm-processor.js');

  // Force stereo throughout; ffmpeg is told to expect exactly that.
  const merger = ctx.createGain();
  merger.channelCount = 2;
  merger.channelCountMode = 'explicit';
  merger.channelInterpretation = 'speakers';

  let anySource = false;
  const problems = [];

  if (wantSystem) {
    try {
      systemStream = await getSystemAudio();
      const src = ctx.createMediaStreamSource(systemStream);
      systemGain = ctx.createGain();
      systemGain.gain.value = settings.systemAudioMuted ? 0 : (settings.systemAudioGain ?? 1);
      src.connect(systemGain).connect(merger);
      anySource = true;
    } catch (err) {
      problems.push(`system audio unavailable (${err.message})`);
    }
  }

  if (wantMic) {
    try {
      micStream = await getMic(settings.micDeviceId);
      const src = ctx.createMediaStreamSource(micStream);
      micGain = ctx.createGain();
      micGain.gain.value = settings.micMuted ? 0 : (settings.micGain ?? 1);
      // Mic is mono; spread it across both channels so it is not stuck left.
      const up = ctx.createGain();
      up.channelCount = 2;
      up.channelCountMode = 'explicit';
      src.connect(micGain).connect(up).connect(merger);
      anySource = true;
    } catch (err) {
      problems.push(`microphone unavailable (${err.message})`);
    }
  }

  if (!anySource) {
    window.audioBridge.ready({ ok: false, error: problems.join('; ') || 'no audio sources' });
    return;
  }

  node = new AudioWorkletNode(ctx, 'pcm-processor', {
    numberOfInputs: 1,
    numberOfOutputs: 0,
    channelCount: 2,
    channelCountMode: 'explicit'
  });
  node.port.onmessage = e => {
    // e.data is an Int16Array whose buffer was transferred to us.
    window.audioBridge.pcm(e.data.buffer);
  };
  merger.connect(node);

  await ctx.resume();

  if (problems.length) report(problems.join('; '));
  window.audioBridge.ready({ ok: true, system: !!systemStream, mic: !!micStream });
}

function applyConfig(settings) {
  if (!ctx) return;
  const t = ctx.currentTime;
  // Short ramps instead of hard steps - a hard gain jump is an audible click
  // in the recording.
  if (systemGain) {
    const v = settings.systemAudioMuted ? 0 : (settings.systemAudioGain ?? 1);
    systemGain.gain.setTargetAtTime(v, t, 0.01);
  }
  if (micGain) {
    const v = settings.micMuted ? 0 : (settings.micGain ?? 1);
    micGain.gain.setTargetAtTime(v, t, 0.01);
  }
}

async function enumerate() {
  try {
    // Labels are only populated once a mic permission has been granted.
    let temp = null;
    try { temp = await navigator.mediaDevices.getUserMedia({ audio: true }); } catch (_) {}
    const devices = await navigator.mediaDevices.enumerateDevices();
    if (temp) for (const t of temp.getTracks()) t.stop();

    window.audioBridge.devices(devices
      .filter(d => d.kind === 'audioinput')
      .map(d => ({ deviceId: d.deviceId, label: d.label || 'Microphone' })));
  } catch (err) {
    window.audioBridge.devices([]);
  }
}

function teardown() {
  try { if (node) { node.port.postMessage({ type: 'stop' }); node.disconnect(); } } catch (_) {}
  for (const s of [systemStream, micStream]) {
    if (s) for (const t of s.getTracks()) t.stop();
  }
  try { if (ctx) ctx.close(); } catch (_) {}
  ctx = node = systemStream = micStream = systemGain = micGain = null;
  started = false;
}

window.audioBridge.onStart(s => {
  start(s).catch(err => window.audioBridge.ready({ ok: false, error: err.message }));
});
window.audioBridge.onConfig(applyConfig);
window.audioBridge.onEnumerate(enumerate);
window.addEventListener('beforeunload', teardown);
