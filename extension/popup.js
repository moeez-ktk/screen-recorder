/** Popup: pairing, plus the gesture-backed "record this tab" entry point. */

const $ = id => document.getElementById(id);

function ask(msg) {
  return new Promise(resolve => chrome.runtime.sendMessage(
    Object.assign({ target: 'sw' }, msg),
    r => resolve(r || {})
  ));
}

async function refresh() {
  const s = await ask({ type: 'status' });
  const capturing = s.capturingTabId != null;

  $('dot').classList.toggle('on', !!s.connected);
  $('status').textContent = !s.token
    ? 'Not paired yet. Paste the token from the app’s Settings → Advanced.'
    : s.connected
      ? (capturing ? 'Recording a tab.' : 'Connected to Light Recorder.')
      : 'Cannot reach the recorder. Is the app running?';

  $('token').value = s.token || '';
  $('port').value = s.port || 8787;

  $('btnRecord').hidden = capturing;
  $('btnStop').hidden = !capturing;
  $('btnRecord').disabled = !s.connected;
}

$('btnSave').addEventListener('click', async () => {
  await ask({ type: 'save', token: $('token').value.trim(), port: Number($('port').value) });
  setTimeout(refresh, 700);
});

$('btnRecord').addEventListener('click', async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab) return;
  $('btnRecord').disabled = true;
  await ask({ type: 'recordTab', tabId: tab.id, opts: { fps: 60, bitrateMbps: 12, tabAudioEnabled: true } });
  setTimeout(() => { refresh(); window.close(); }, 500);
});

$('btnStop').addEventListener('click', async () => {
  await ask({ type: 'stopTab' });
  setTimeout(refresh, 400);
});

// Offscreen documents cannot show a permission prompt, so the grant has to
// happen here, in a real extension page, once.
$('btnMic').addEventListener('click', async () => {
  try {
    const s = await navigator.mediaDevices.getUserMedia({ audio: true });
    for (const t of s.getTracks()) t.stop();
    $('btnMic').textContent = 'Microphone allowed';
    $('btnMic').disabled = true;
  } catch (_) {
    $('btnMic').textContent = 'Microphone permission denied';
  }
});

refresh();
