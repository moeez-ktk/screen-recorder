# Light Recorder

A lightweight streaming screen recorder for Windows. Press a hotkey, get a small
overlay in the corner, hit record. Everything else lives behind a settings icon.

Built to cost as little as possible: **no video frame ever enters the app's
memory, and on a GPU that can take it, no video frame ever enters main memory at
all.** ffmpeg captures through the Desktop Duplication API and hands the D3D11
texture straight to the hardware encoder. Memory is flat whether you record for
thirty seconds or eight hours.

---

## Requirements

- Windows 10 2004+ or Windows 11
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) on `PATH`, or point at it in
  Settings → Advanced. A `full_build` is recommended (it includes `ddagrab`).

That is the whole list. There is no runtime to install, no Node, no package
manager, and no SDK — the app is a single 221 KB executable built by the C#
compiler that ships inside Windows.

## Building

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output is `build\LightRecorder.exe`. Add `-Run` to launch it, or `-Dev` for an
unoptimised build with a console attached.

Then just run it. It puts an icon in the tray, registers its shortcuts, and
stays out of the way. It also arranges to start again, in the tray, every time
you sign in, so the shortcuts survive a reboot. That is *Start with Windows* in
Settings → Shortcuts, on by default, and it is a per-user Task Scheduler task
named "Light Recorder" rather than a Run-key entry — Troubleshooting says why.

---

## The shortcut

**`Ctrl+Shift+D` shows the recorder.** Press it and the overlay appears in the
top-right, ready to record. Press it again to dismiss it.

> A global hotkey is claimed system-wide, so `Ctrl+Shift+D` will shadow
> **VS Code's Run and Debug panel** and Chrome's *Bookmark all tabs* for as long
> as the app runs. Change it under Settings → Shortcuts if you want those back.

| Shortcut | Does |
| --- | --- |
| **`Ctrl+Shift+D`** | **Show the recorder** / dismiss it again |
| `Ctrl+Alt+S` | Start or stop recording |
| `Alt+Shift+P` | Pause / resume recording |
| `Ctrl+Alt+X` | Stop recording |
| `Ctrl+Alt+M` | Mute / unmute the microphone |
| `Ctrl+Alt+P` | Choose what to record |

All are rebindable in Settings and work from any application, including from
inside a fullscreen game. They act immediately — there is nothing to launch
first.

If another application already owns a combination, Windows silently refuses to
register it and the shortcut simply never fires. Settings → Shortcuts says so
plainly rather than leaving you to guess.

---

## What it costs

One process. It owns the hotkeys, the tray icon, the overlay, the audio capture
and the browser bridge, and it is the only thing that stays resident.

Measured on an RTX 2060 Super at 1080p60, comparing against the Electron build
this replaces:

| | Processes | Resident | Committed |
| --- | --- | --- | --- |
| Electron build, overlay showing, idle | 4 | 285.2 MB | 132.8 MB |
| **This build, overlay showing, idle** | **1** | **2.1 MB** | **24.5 MB** |
| This build, overlay dismissed | 1 | 0.8 MB | 24.5 MB |
| This build, recording | 1 | 23.6 MB | 27.3 MB |

CPU while recording: **2.1% of one core** for the app, **7.5%** for ffmpeg,
measured over a four-minute take. Over those four minutes both processes' memory
was flat — the app sat at 49.7 MB resident from 100 s to 200 s without moving a
kilobyte, and ffmpeg held 153 MB throughout.

Idle really is under a megabyte: when the overlay is dismissed the app hands its
pages back to the OS and sits blocked in `GetMessage`. The ~25 MB of *committed*
address space behind that is the .NET heap, which is reserved rather than
resident.

Nothing polls when idle. Thumbnails are only produced while the picker is open,
the audio graph only exists while recording, and the session list is only read
while the mixer is on screen.

The overlay is a single pill: status and timer, then **record** and **stop**,
then source, microphone, system audio, mixer, and settings. Once recording, the
record button becomes **pause**, and while paused it becomes **resume** — the red
dot again — while the timer stands still and the pill turns amber.

**It collapses while recording.** The moment capture starts, the pill shrinks to
just the elapsed time, pause, stop, and a `‹` chevron. Press the chevron to
expand back to the full set of controls. Stopping restores the full pill
automatically. The window is anchored by its right edge, so collapsing pulls the
left side in instead of sliding the whole thing around.

By default it records your **entire primary screen** and never asks anything.
Press `Ctrl+Alt+P` to pick a different screen, an application window, or a
browser tab; the chooser opens centred on whichever monitor your cursor is on.

The overlay and its panels are marked `WDA_EXCLUDEFROMCAPTURE`, so they never
appear in your own recordings.

### Pausing

A pause leaves the paused stretch out of the file entirely: one file, one
encoder session, and stopping is still instant. There is no gap and no frozen
frame at the join, and sound and picture stay lined up across any number of
pauses — measured by flashing a square and playing a click together, the offset
after a pause matches the offset before it to within a frame.

Recording a single browser tab pauses in the browser, where MediaRecorder does
it natively. Reload the extension after updating the app to pick that up.

---

## Audio

Three independent layers, all switchable live while recording:

- **System audio** — everything you hear, captured as WASAPI loopback.
- **Microphone** — mixed in with its own gain, and levelled automatically.
- **Per-source muting** — the mixer button opens a panel listing every
  application currently holding an audio session and, when the extension is
  connected, every browser tab.

Muting an app or tab there silences it *in the recording and on your speakers*
— that is inherent to how Windows and Chrome expose the control, and everything
is restored to exactly how you left it when you stop.

**Recording a single tab is the exception.** Only that tab is captured, so
another tab playing music is simply not in the file and nothing has to be muted.

### Microphone level

A microphone recorded raw is exactly as loud as the microphone is, and most are
quiet: speech lands around -35 dBFS, far under the system audio it is mixed
with. Browser-based recorders hide this because Chromium runs automatic gain
control on every microphone. *Level the microphone automatically* (Settings →
Audio, on by default) does the same job: an 80 Hz high-pass, then a gain that
eases toward a steady -18 dBFS speaking level, up to +20 dB. It only moves while
it hears a voice — judged against the microphone's own noise floor and by how
voiced the sound is — so neither the hiss between sentences nor typing gets
lifted to speaking level. A peak limiter at the end of the mix keeps microphone
plus system audio from clipping.

Turn it off for the raw signal; the Mic volume slider then goes to 400%. Either
way, the microphone's own input level in Windows (Settings → System → Sound →
your microphone) is the cleanest place to add gain, since it comes before any
digital boost.

### Why the browser extension exists

Windows cannot separate one browser tab's audio from another's — every tab
shares the browser's audio process, so there is no supported way for a desktop
app to record tab A while excluding tab B. Only the browser can do that. The
extension therefore handles tab listing, per-tab muting, and tab-scoped capture;
the desktop app handles everything else, including games and non-browser apps.

### Installing the extension

1. Settings → Advanced → **Copy** the pairing token.
2. Go to `chrome://extensions` (or `edge://extensions`), enable **Developer
   mode**, choose **Load unpacked**, and select this repo's `extension` folder.
3. Click the extension icon, paste the token, press **Save and connect**.

Chrome only permits an extension to capture a tab it has been *invoked* on. If
starting a tab recording from the app's picker fails, use **Record this tab** in
the extension popup instead — that click satisfies the requirement. For
voice-over on tab recordings, press **Allow microphone** in the popup once.

---

## Output

Fragmented MP4 (H.264 + AAC). Two useful properties:

- The file is valid and playable at **every instant**, so a crash or power cut
  costs you the last fragment rather than the whole recording. This is tested,
  not hoped for: killing the app mid-take leaves a file that decodes without a
  single error.
- Stopping is instant — there is no muxing wait proportional to length.

Some editors prefer a `moov` atom at the front. Settings → *Optimise for video
editors* adds a fast stream-copy pass on stop that does this. It is off by
default because it makes stopping non-instant on long recordings.

Files land in `Videos\Light Recorder` unless you change the folder.

---

## Architecture

```
  ┌─ one process, always resident ─────────────────────────────────┐
  │ LightRecorder.exe        221 KB on disk, 2 MB resident         │
  │                                                                │
  │   RegisterHotKey ──▶ WM_HOTKEY                                 │
  │   overlay / picker / settings / mixer   drawn with GDI+        │
  │   WASAPI loopback ─┐                                           │
  │   WASAPI mic ─▶ AGC┼─▶ pacer ─▶ limiter ─▶ s16le┐              │
  │   Core Audio sessions      per-app mute         │              │
  │   RFC 6455 server          browser extension    │              │
  └─────────────────────────────────────────────────┼──────────────┘
                                      named pipe    ▼
   screen ──▶ ddagrab (DXGI) ──▶ D3D11 texture ──▶ NVENC/QSV/AMF ──▶ .mp4
                                 never leaves the GPU
```

Why it stays light:

- **Video never touches the CPU.** `ddagrab` produces D3D11 textures and the
  hardware encoder consumes D3D11 textures, so on NVENC and AMF the frames go
  from the desktop to the encoder without a single copy through main memory.
  See *The pipeline* below — this is the single largest saving in the app.
- **Audio is bounded.** Two one-second ring buffers and a pacer that emits
  exactly as much PCM as wall-clock time says it should, filling silence when a
  source is dry. Total audio memory in flight is under a megabyte and never
  grows.
- **Both tracks count from one instant.** Video frames are stamped with the
  wall clock as they are captured, the audio pacer's timeline starts at the
  moment ffmpeg is launched, and `setpts` subtracts that same moment from every
  frame. Nothing depends on the order ffmpeg happens to open its inputs in; left
  to itself it starts each input's clock at that input's first packet. Measured
  by flashing a square and playing a click together, this moved the picture
  75 ms later than the previous build had it — which had it early — and 150 ms
  earlier than ffmpeg's own alignment would.
- **Tab chunks stream too.** The offscreen document holds its own socket and
  writes through to disk, with backpressure that drops instead of buffering.
- **The UI allocates nothing per frame.** Icon geometry, drop shadows, fonts
  and label strings are all built once and reused, so the overlay's recording
  animation runs at eight frames a second for hours without moving the heap.

### The pipeline

The obvious way to wire ffmpeg up is the way this app used to:

```
ddagrab → hwdownload → format=bgra → scale → format=nv12 → h264_nvenc
```

That pulls every frame off the GPU, converts it on the CPU, and hands it back to
the GPU to encode. Both hardware encoders here accept D3D11 frames directly, so
the entire middle of that chain can go:

```
ddagrab → h264_nvenc
```

Measured on an RTX 2060 Super, 20 seconds of 1080p60 capture, byte-identical
output size:

| | ffmpeg CPU | ffmpeg peak RSS |
| --- | --- | --- |
| download path | **61.2 s** (≈3 cores saturated) | 215 MB |
| D3D11 direct | **1.0 s** | 147 MB |

The app proves the path at first run with a real two-frame capture rather than
assuming it, caches the answer, and falls back to the download path when the
encoder cannot take D3D11 or when you ask for a resolution other than Native —
scaling is the one thing that forces frames back through the CPU. Settings →
Encoder says which path you are on.

ddagrab is opened as a `-f lavfi` input rather than written inside
`-filter_complex`, followed by a lone `setpts`. That filter only rewrites
timestamps, so the frames still never leave the GPU (1.0 s of ffmpeg CPU for 20 s
of capture, unchanged) — and a graph fed by an input is one ffmpeg delivers
runtime commands to, which is what pausing is built on. A graph with nothing but
a source in it never reads them.

### Layout

```
app/            Program.cs        entry point, single instance, IPC
                App.cs            orchestration, tray, recording lifecycle
                Recorder.cs       ffmpeg process, argument builder, audio pipe
                AudioEngine.cs    WASAPI capture, mixing, the pacer
                VoiceLeveler.cs   microphone auto-level, output limiter
                SessionMixer.cs   Core Audio per-application mute
                Encoders.cs       ffmpeg discovery and capability probing
                Sources.cs        monitors, windows, thumbnails
                Dxgi.cs           output enumeration for ddagrab
                Bridge.cs         extension protocol
                WebSocketServer.cs  RFC 6455, ~400 lines, no dependencies
                Hotkeys.cs        RegisterHotKey, accelerators
                Autostart.cs      the logon task behind Start with Windows
                Settings.cs       persisted settings
                Json.cs           a JSON reader and writer in one file
                Native.cs         the Win32 surface
                Interop.cs        Core Audio COM definitions
app/Ui/         Theme.cs          the design tokens from the old theme.css
                Icons.cs          SVG path parser; icon data copied verbatim
                LayeredWindow.cs  per-pixel-alpha windows (overlay, mixer)
                PaintedForm.cs    opaque dialogs (picker, settings)
                Widgets.cs        drawn switch, slider, select, button
                OverlayForm.cs, MixerWindow.cs, PickerForm.cs,
                SettingsForm.cs, Toasts.cs
extension/      MV3 bridge: tabs, per-tab mute, tab capture
assets/         make-icon.ps1 generates the app icon
legacy/         the previous Electron implementation, kept for reference
```

---

## Notes and limits

- **Encoders are proven, not assumed.** At first run each candidate is tested
  with a real two-frame encode, and the result is cached. Hardware encoders
  need NV12 input on the CPU path — feeding NVENC planar `yuv420p` makes it fail
  outright with `CreateInputBuffer failed`, so the pixel format is chosen per
  encoder.
- **A window is recorded as a region of its screen.** The window's rectangle
  is looked up when recording starts and that part of the monitor goes
  through `ddagrab`, on the same GPU path as a full screen. The alternative,
  GDI window capture, records every hardware-accelerated window — browsers,
  games, Electron apps — as solid black, and found windows by title, so a
  browser whose title changes with the active tab could not be started at all
  (`I/O error`). The trade is that the region is fixed for the take: move the
  window and the recording stays where it was, and a window dragged over it is
  recorded too. Minimised or closed windows are refused with a message rather
  than recorded as nothing. Only when ffmpeg has no `ddagrab` does window
  capture fall back to GDI, by window handle.
- **Per-app mute is not per-app *recording*.** Isolating one app's audio into a
  recording while leaving it audible needs a kernel driver; muting the session
  is the supported alternative, and it works identically on Windows 10 and 11.
- The bridge listens on `127.0.0.1` only and requires the pairing token, so a
  random web page cannot drive it.
- ffmpeg is started inside a job object that dies with the app, so a crash
  cannot leave a stray encoder recording your screen to disk.

## Troubleshooting

Two logs, both under `%APPDATA%\Light Recorder\`:

- **`app.log`** — warnings and failures, trimmed to the last 64 KB. Which
  encoders offered the GPU path, why audio could not open, why the bridge port
  was refused.
- **`last-recording.log`** — the exact ffmpeg command line, which pipeline it
  used, how long ffmpeg took to open the audio pipe, and everything ffmpeg
  said if it failed — whether you stopped it or it died on its own. If a
  recording comes out empty or short, this says why.

**Settings, logs or autostart that seem to belong to another copy?** A program
started from inside a packaged (MSIX) app — a terminal or editor installed as a
package, and anything it launches, `build.ps1 -Run` included — inherits that
package's virtualisation: its writes to `%APPDATA%` and `HKCU` land in the
package's private copy under `%LOCALAPPDATA%\Packages\`. A copy of the app
launched that way reads and writes its own settings and logs, invisible to the
copy started from Explorer or at sign-in. This is why autostart is a Task
Scheduler task: the old Run-key entry, written from such a launch, was never
seen by Explorer, and the shortcuts were dead after every reboot.

**Working on the UI?** Set `LIGHTRECORDER_ALLOW_CAPTURE=1` before launching. The
overlay is normally excluded from every form of screen capture, which also makes
it invisible to screenshots; this turns that off.
