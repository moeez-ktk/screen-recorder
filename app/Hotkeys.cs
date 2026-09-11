using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LightRecorder {

  /// <summary>
  /// Global hotkeys, owned in-process.
  ///
  /// The Electron build could not do this. Keeping a two-hundred-megabyte
  /// runtime alive purely to watch for a keypress was indefensible, so it
  /// shipped a separate 116 KB C# listener that forwarded commands down a named
  /// pipe and launched the app when it was not running - a second executable, a
  /// config file, a log file, an autostart entry and a one-second cold start,
  /// all to work around the weight of the thing it was launching.
  ///
  /// At this size the app just registers the hotkeys itself. RegisterHotKey is
  /// exclusive, there is only one owner, and pressing a shortcut acts
  /// immediately instead of booting a browser first.
  /// </summary>
  internal class HotkeyManager : IDisposable {

    /// <summary>Raised on the UI thread with the command name.</summary>
    public event Action<string> Pressed;

    readonly MessageWindow _window;
    readonly List<int> _registered = new List<int>();
    readonly Dictionary<int, string> _commands = new Dictionary<int, string>();

    /// <summary>Combinations another application already owns. Those silently
    /// never fire, which is otherwise impossible to diagnose.</summary>
    public readonly List<string> Conflicts = new List<string>();

    public HotkeyManager() {
      _window = new MessageWindow(this);
    }

    public IntPtr Handle { get { return _window.Handle; } }

    // ------------------------------------------------------------ registration

    /// <summary>Drop every current binding and register the current settings.</summary>
    public void Apply(Hotkeys hotkeys) {
      Unregister();
      Conflicts.Clear();

      int id = 1;
      foreach (string name in Hotkeys.Names) {
        string accel = hotkeys.Get(name);
        if (string.IsNullOrEmpty(accel)) continue;

        uint mods, vk;
        if (!TryParse(accel, out mods, out vk)) {
          Log.Warn("hotkey: could not parse " + accel + " for " + name);
          continue;
        }

        // MOD_NOREPEAT so holding the key down fires once, not sixty times.
        if (Native.RegisterHotKey(_window.Handle, id, mods | Native.MOD_NOREPEAT, vk)) {
          _registered.Add(id);
          _commands[id] = name;
        } else {
          Conflicts.Add(accel);
          Log.Warn("hotkey: " + accel + " is already claimed by another application");
        }
        id++;
      }
    }

    public void Unregister() {
      foreach (int id in _registered) {
        try { Native.UnregisterHotKey(_window.Handle, id); } catch (Exception) { }
      }
      _registered.Clear();
      _commands.Clear();
    }

    internal void Fire(int id) {
      string command;
      if (!_commands.TryGetValue(id, out command)) return;
      Action<string> h = Pressed;
      if (h != null) h(command);
    }

    public void Dispose() {
      Unregister();
      _window.Destroy();
    }

    // ------------------------------------------------------------- parsing

    /// <summary>
    /// Parse an accelerator in the form the settings file already stores -
    /// "Control+Shift+D" - so an upgrade keeps the user's shortcuts.
    /// </summary>
    public static bool TryParse(string accel, out uint modifiers, out uint vk) {
      modifiers = 0;
      vk = 0;
      if (string.IsNullOrEmpty(accel)) return false;

      string[] parts = accel.Split('+');
      string keyPart = null;

      foreach (string raw in parts) {
        string part = raw.Trim();
        if (part.Length == 0) continue;

        switch (part.ToLowerInvariant()) {
          case "control": case "ctrl": case "commandorcontrol": case "cmdorctrl":
            modifiers |= Native.MOD_CONTROL; break;
          case "alt": case "option":
            modifiers |= Native.MOD_ALT; break;
          case "shift":
            modifiers |= Native.MOD_SHIFT; break;
          case "super": case "meta": case "command": case "cmd": case "win":
            modifiers |= Native.MOD_WIN; break;
          default:
            keyPart = part; break;
        }
      }

      if (keyPart == null) return false;
      vk = KeyToVk(keyPart);
      if (vk == 0) return false;

      // A bare letter would swallow that key system-wide. Function keys are the
      // one exception people genuinely want unmodified.
      if (modifiers == 0 && !(vk >= 0x70 && vk <= 0x87)) return false;
      return true;
    }

    static uint KeyToVk(string key) {
      if (key.Length == 1) {
        char c = char.ToUpperInvariant(key[0]);
        if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        short scan = VkKeyScan(key[0]);
        if (scan != -1) return (uint)(scan & 0xFF);
        return 0;
      }

      if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f')) {
        int n;
        if (int.TryParse(key.Substring(1), out n) && n >= 1 && n <= 24) return (uint)(0x6F + n);
      }

      switch (key.ToLowerInvariant()) {
        case "space": return 0x20;
        case "escape": case "esc": return 0x1B;
        case "tab": return 0x09;
        case "enter": case "return": return 0x0D;
        case "backspace": return 0x08;
        case "delete": case "del": return 0x2E;
        case "insert": return 0x2D;
        case "home": return 0x24;
        case "end": return 0x23;
        case "pageup": return 0x21;
        case "pagedown": return 0x22;
        case "up": return 0x26;
        case "down": return 0x28;
        case "left": return 0x25;
        case "right": return 0x27;
        case "printscreen": return 0x2C;
        case "pause": return 0x13;
        case "capslock": return 0x14;
        case "numlock": return 0x90;
        case "scrolllock": return 0x91;
        case "plus": return 0xBB;
        case "minus": return 0xBD;
      }
      return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern short VkKeyScan(char ch);

    /// <summary>
    /// Turn a keystroke captured in the Settings window into the same
    /// accelerator string the settings file stores. Returns null for a
    /// modifier on its own, or for anything that would be unsafe to claim.
    /// </summary>
    public static string FromKeyEvent(KeyEventArgs e) {
      Keys key = e.KeyCode;
      if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey ||
          key == Keys.LWin || key == Keys.RWin || key == Keys.None) return null;

      var parts = new List<string>();
      if (e.Control) parts.Add("Control");
      if (e.Alt) parts.Add("Alt");
      if (e.Shift) parts.Add("Shift");

      string name = KeyName(key);
      if (name == null) return null;

      bool isFunctionKey = key >= Keys.F1 && key <= Keys.F24;
      if (parts.Count == 0 && !isFunctionKey) return null;

      parts.Add(name);
      return string.Join("+", parts.ToArray());
    }

    static string KeyName(Keys key) {
      if (key >= Keys.A && key <= Keys.Z) return key.ToString();
      if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
      if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return ((char)('0' + (key - Keys.NumPad0))).ToString();
      if (key >= Keys.F1 && key <= Keys.F24) return "F" + (key - Keys.F1 + 1);

      switch (key) {
        case Keys.Space: return "Space";
        case Keys.Escape: return "Escape";
        case Keys.Tab: return "Tab";
        case Keys.Enter: return "Enter";
        case Keys.Back: return "Backspace";
        case Keys.Delete: return "Delete";
        case Keys.Insert: return "Insert";
        case Keys.Home: return "Home";
        case Keys.End: return "End";
        case Keys.PageUp: return "PageUp";
        case Keys.PageDown: return "PageDown";
        case Keys.Up: return "Up";
        case Keys.Down: return "Down";
        case Keys.Left: return "Left";
        case Keys.Right: return "Right";
        case Keys.Oemplus: return "Plus";
        case Keys.OemMinus: return "Minus";
      }
      return null;
    }

    /// <summary>Human-readable form for tooltips: "Ctrl+Alt+S".</summary>
    public static string Display(string accel) {
      if (string.IsNullOrEmpty(accel)) return "not set";
      return accel.Replace("Control", "Ctrl").Replace("Super", "Win");
    }

    // -------------------------------------------------------- message window

    /// <summary>
    /// A message-only window: no pixels, no taskbar presence, no paint cycle.
    /// It exists solely so the OS has somewhere to post WM_HOTKEY.
    /// </summary>
    class MessageWindow : NativeWindow {
      readonly HotkeyManager _owner;

      public MessageWindow(HotkeyManager owner) {
        _owner = owner;
        var cp = new CreateParams();
        cp.Caption = "LightRecorderHotkeys";
        cp.Parent = Native.HWND_MESSAGE;
        CreateHandle(cp);
      }

      protected override void WndProc(ref Message m) {
        if (m.Msg == (int)Native.WM_HOTKEY) {
          _owner.Fire(m.WParam.ToInt32());
          return;
        }
        base.WndProc(ref m);
      }

      public void Destroy() {
        try { DestroyHandle(); } catch (Exception) { }
      }
    }
  }
}
