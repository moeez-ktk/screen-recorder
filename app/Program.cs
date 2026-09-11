using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LightRecorder {

  internal static class Program {

    const string MutexName = @"Global\LightRecorderSingleInstance";
    const string IpcWindowTitle = "LightRecorderIPC";

    /// <summary>Commands that can arrive from a second launch. The index is
    /// what travels in the message, so the order is part of the contract with
    /// ourselves and only ever grows at the end.</summary>
    static readonly string[] Commands = {
      "launch", "show", "startStop", "record", "stop", "toggleMic", "picker", "toggleCompact", "quit",
      "settings", "mixer", "toggleSystemAudio", "togglePause"
    };

    [STAThread]
    static int Main(string[] argv) {
      // Before any window exists, or Windows decides for us and everything is
      // bitmap-stretched on a high-DPI display.
      Native.SetupDpiAwareness();

      string command = CommandFromArgv(argv);
      bool atLogon = HasArg(argv, Autostart.Argument);

      bool createdNew;
      using (var mutex = new Mutex(true, MutexName, out createdNew)) {
        if (!createdNew) {
          // Already running. Hand the command over and get out of the way -
          // unless this is the logon task finding a copy the user beat it to,
          // which has nothing to ask of it.
          if (!atLogon) Forward(command ?? "launch");
          return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) {
          Log.Warn("unhandled UI exception: " + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) {
          Log.Warn("unhandled exception: " + e.ExceptionObject);
        };

        var app = new App();
        IpcWindow ipc = null;

        try {
          app.Start(command, atLogon);
          ipc = new IpcWindow(app);
          Application.Run();
        } catch (Exception err) {
          Log.Warn("fatal: " + err);
          throw;
        } finally {
          if (ipc != null) ipc.Destroy();
          app.Dispose();
          GC.KeepAlive(mutex);
        }
      }
      return 0;
    }

    /// <summary>Kept for compatibility with anything that used to launch the
    /// old build with --cmd=&lt;name&gt;.</summary>
    static string CommandFromArgv(string[] argv) {
      if (argv == null) return null;
      foreach (string a in argv) {
        if (a != null && a.StartsWith("--cmd=", StringComparison.Ordinal))
          return a.Substring(6).Trim();
      }
      return null;
    }

    static bool HasArg(string[] argv, string flag) {
      if (argv == null) return false;
      foreach (string a in argv) {
        if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
      }
      return false;
    }

    // ------------------------------------------------------------------ IPC

    static readonly uint IpcMessage = RegisterWindowMessage("LightRecorderCommand");

    static void Forward(string command) {
      IntPtr target = FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, IpcWindowTitle);
      if (target == IntPtr.Zero) return;

      int index = Array.IndexOf(Commands, command);
      if (index < 0) index = 0;                       // unknown means "just show yourself"
      PostMessage(target, IpcMessage, new IntPtr(index), IntPtr.Zero);
    }

    /// <summary>
    /// A message-only window that exists so a second launch of the exe can say
    /// what it wanted and exit, rather than opening a second recorder.
    /// </summary>
    class IpcWindow : NativeWindow {
      readonly App _app;

      public IpcWindow(App app) {
        _app = app;
        var cp = new CreateParams();
        cp.Caption = IpcWindowTitle;
        cp.Parent = Native.HWND_MESSAGE;
        CreateHandle(cp);
      }

      protected override void WndProc(ref Message m) {
        if (m.Msg == (int)IpcMessage) {
          int index = m.WParam.ToInt32();
          if (index >= 0 && index < Commands.Length) _app.RunCommand(Commands[index]);
          return;
        }
        base.WndProc(ref m);
      }

      public void Destroy() {
        try { DestroyHandle(); } catch (Exception) { }
      }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  }
}
