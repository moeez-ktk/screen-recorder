using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LightRecorder {

  /// <summary>
  /// Start with Windows, as a per-user logon task.
  ///
  /// What starts is the hotkey listener when it is installed next to the app,
  /// and the app itself otherwise. The listener is the process meant to stay
  /// resident; the app only runs while its overlay is up.
  ///
  /// This used to be a value under HKCU\...\Run, and after a reboot it quietly
  /// did nothing. A process started from inside a packaged (MSIX) app - an
  /// editor or a terminal installed as a package - inherits that package's
  /// registry virtualisation, so its write to HKCU\Run landed in the package's
  /// private hive. The app read the value straight back and reported autostart
  /// as on; Explorer, reading the real hive at logon, never saw it.
  ///
  /// A scheduled task is stored by the Task Scheduler service rather than
  /// written into whichever view of the registry this process was handed, so
  /// the registration is real however the app was launched. It also fires the
  /// moment the user signs in, where the Run list waits for Explorer to get
  /// round to it - half a minute after the desktop appears, on the machine this
  /// was diagnosed on.
  /// </summary>
  internal static class Autostart {

    const string TaskName = "Light Recorder";

    /// <summary>Tells a start at sign-in to stay in the tray instead of
    /// putting the overlay on screen.</summary>
    public const string Argument = "--autostart";

    const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyRunValue = "LightRecorder";

    static readonly object Gate = new object();

    /// <summary>Why the last sync failed, or null. Kept for the Settings
    /// window: finding out afresh means starting schtasks, which is not
    /// something to do on every paint.</summary>
    public static volatile string LastError;

    /// <summary>Raised on a worker thread after every sync.</summary>
    public static event Action Changed;

    /// <summary>
    /// Bring the task in line with the setting, on a worker: every step is a
    /// process start. When it already matches, this costs one query.
    /// </summary>
    public static void SyncAsync(bool enabled) {
      ThreadPool.QueueUserWorkItem(delegate {
        lock (Gate) Sync(enabled);
        Action h = Changed;
        if (h != null) { try { h(); } catch (Exception) { } }
      });
    }

    static void Sync(bool enabled) {
      // Either way the old Run value goes: alongside the task it would start a
      // second copy at logon, and with autostart off it should start nothing.
      RemoveLegacyRunValue();

      bool listener = Listener.Exists;
      string exe = listener ? Listener.ExePath : Application.ExecutablePath;
      string arguments = listener ? "" : Argument;
      bool present;
      bool current = IsCurrent(exe, arguments, out present);
      string error = null;

      if (enabled && !current) {
        error = Create(exe, arguments);
        if (error == null) Log.Info("autostart: logon task " + (present ? "re-pointed at " : "registered for ") + exe);
      } else if (!enabled && present) {
        error = Delete();
        if (error == null) Log.Info("autostart: logon task removed");
      }

      if (error != null) Log.Warn("autostart: " + error);
      LastError = error;
    }

    /// <summary>Does the task exist, and does it start this exe the way we
    /// would register it? A copy of the app that has moved, a task from an
    /// older build, or one disabled by hand is re-registered, not trusted.</summary>
    static bool IsCurrent(string exe, string arguments, out bool present) {
      string xml;
      present = Run("/Query /TN \"" + TaskName + "\" /XML", out xml) == 0;
      if (!present) return false;

      string command = (Element(xml, "Command") ?? "").Trim().Trim('"');
      string stored = (Element(xml, "Arguments") ?? "").Trim();
      return string.Equals(command, exe, StringComparison.OrdinalIgnoreCase) &&
             stored == arguments &&
             xml.IndexOf("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) < 0;
    }

    static string Create(string exe, string arguments) {
      string file = Path.Combine(Path.GetTempPath(), "LightRecorder-task-" + Process.GetCurrentProcess().Id + ".xml");
      try {
        // UTF-16 to match the declaration; schtasks rejects a mismatch.
        File.WriteAllText(file, TaskXml(exe, arguments, WindowsIdentity.GetCurrent().User.Value), Encoding.Unicode);
        string output;
        int code = Run("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out output);
        return code == 0 ? null : "could not register the logon task (" + FirstLine(output, code) + ")";
      } catch (Exception err) {
        return "could not register the logon task (" + err.Message + ")";
      } finally {
        try { File.Delete(file); } catch (Exception) { }
      }
    }

    static string Delete() {
      string output;
      int code = Run("/Delete /TN \"" + TaskName + "\" /F", out output);
      return code == 0 ? null : "could not remove the logon task (" + FirstLine(output, code) + ")";
    }

    /// <summary>
    /// The task definition. Three of its settings exist only to undo Task
    /// Scheduler defaults that would quietly break a resident app:
    ///
    ///   ExecutionTimeLimit  the default kills the task after three days
    ///   ...OnBatteries      the defaults refuse to start it on battery and
    ///                       stop it when the charger is pulled
    ///   Priority            the default of 7 is below normal, and children
    ///                       inherit it - ffmpeg would encode, and the audio
    ///                       pacer would run, at below-normal CPU and I/O
    ///                       priority for the whole session
    /// </summary>
    static string TaskXml(string exe, string arguments, string sid) {
      string command = SecurityElement.Escape(exe);
      string args = SecurityElement.Escape(arguments ?? "");
      string dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
      string user = SecurityElement.Escape(sid);
      return
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
        "  <RegistrationInfo>\r\n" +
        "    <Description>Starts Light Recorder's hotkey listener when you sign in, so its shortcut " +
        "works straight away. Managed by the app under Settings, Shortcuts, Start with Windows.</Description>\r\n" +
        "  </RegistrationInfo>\r\n" +
        "  <Triggers>\r\n" +
        "    <LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger>\r\n" +
        "  </Triggers>\r\n" +
        "  <Principals>\r\n" +
        "    <Principal id=\"Author\"><UserId>" + user + "</UserId>" +
        "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>\r\n" +
        "  </Principals>\r\n" +
        "  <Settings>\r\n" +
        "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
        "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
        "    <Priority>5</Priority>\r\n" +
        "  </Settings>\r\n" +
        "  <Actions Context=\"Author\">\r\n" +
        "    <Exec><Command>" + command + "</Command><Arguments>" + args + "</Arguments>" +
        "<WorkingDirectory>" + dir + "</WorkingDirectory></Exec>\r\n" +
        "  </Actions>\r\n" +
        "</Task>\r\n";
    }

    static void RemoveLegacyRunValue() {
      try {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true)) {
          if (k == null || k.GetValue(LegacyRunValue) == null) return;
          k.DeleteValue(LegacyRunValue, false);
          Log.Info("autostart: removed the old Run entry in favour of the logon task");
        }
      } catch (Exception err) {
        Log.Warn("autostart: could not remove the old Run entry: " + err.Message);
      }
    }

    // ------------------------------------------------------------- helpers

    static int Run(string args, out string output) {
      var text = new StringBuilder();
      try {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using (Process p = Process.Start(psi)) {
          DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e) {
            if (e.Data != null) lock (text) text.Append(e.Data).Append('\n');
          };
          p.OutputDataReceived += collect;
          p.ErrorDataReceived += collect;
          p.BeginOutputReadLine();
          p.BeginErrorReadLine();

          if (!p.WaitForExit(20000)) {
            try { p.Kill(); } catch (Exception) { }
            output = "schtasks timed out";
            return -1;
          }
          p.WaitForExit();                 // lets the output handlers drain
          lock (text) output = text.ToString();
          return p.ExitCode;
        }
      } catch (Exception err) {
        output = err.Message;
        return -1;
      }
    }

    static string Element(string xml, string name) {
      string open = "<" + name + ">", close = "</" + name + ">";
      int a = xml.IndexOf(open, StringComparison.Ordinal);
      if (a < 0) return null;
      a += open.Length;
      int b = xml.IndexOf(close, a, StringComparison.Ordinal);
      if (b < 0) return null;
      return xml.Substring(a, b - a)
                .Replace("&quot;", "\"").Replace("&apos;", "'")
                .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }

    static string FirstLine(string text, int code) {
      foreach (string line in (text ?? "").Split('\n')) {
        string t = line.Trim();
        if (t.Length > 0) return t;
      }
      return "exit code " + code;
    }
  }
}
