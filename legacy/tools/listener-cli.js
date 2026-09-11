'use strict';
/**
 * Thin CLI wrapper around LightRecorderHotkey.exe.
 *
 * The listener is built as a GUI-subsystem binary so no console window flashes
 * up each time a hotkey fires. The trade-off is that its output does not
 * reliably reach a terminal it was launched from. Piping through Node does
 * capture it, so the npm scripts go this way.
 *
 *   node tools/listener-cli.js --status
 *   node tools/listener-cli.js --install
 *   node tools/listener-cli.js --uninstall
 *   node tools/listener-cli.js --start     (detached)
 */
const { execFile, spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const EXE = path.join(__dirname, 'LightRecorderHotkey.exe');

if (!fs.existsSync(EXE)) {
  console.error('Listener is not built yet. Run: npm run listener:build');
  process.exit(1);
}

const arg = process.argv[2] || '--status';

if (arg === '--start') {
  const child = spawn(EXE, [], { detached: true, stdio: 'ignore', windowsHide: true });
  child.unref();
  console.log('Listener started. Hotkeys are live.');
  // Give it a moment to claim the hotkeys before reporting back.
  setTimeout(() => status(), 800);
} else {
  status(arg);
}

function status(flag) {
  execFile(EXE, [flag || '--status'], { windowsHide: true, timeout: 15000 }, (err, stdout, stderr) => {
    if (stdout) process.stdout.write(stdout);
    if (stderr) process.stderr.write(stderr);
    if (err && !stdout && !stderr) console.error(err.message);
    process.exit(err ? 1 : 0);
  });
}
