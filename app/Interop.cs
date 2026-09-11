using System;
using System.Runtime.InteropServices;

namespace LightRecorder {

  // ==========================================================================
  //  Core Audio (WASAPI + the session API)
  //
  //  This file is what lets the app do its own audio. The Electron build had
  //  to spin up a hidden Chromium renderer to reach WebAudio for capture, and
  //  shell out to PowerShell to reach the session API for per-app mute. Both
  //  of those cost more memory than the entire application does now.
  //
  //  COM interfaces are declared in full, base methods included, because a
  //  [ComImport] interface in C# does not inherit its parent's vtable slots.
  // ==========================================================================

  internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
  internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

  internal static class DeviceState {
    public const uint Active = 0x00000001;
    public const uint Disabled = 0x00000002;
    public const uint NotPresent = 0x00000004;
    public const uint Unplugged = 0x00000008;
    public const uint All = 0x0000000F;
  }

  internal static class AudClnt {
    public const int SHAREMODE_SHARED = 0;

    public const uint STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint STREAMFLAGS_NOPERSIST = 0x00080000;

    public const uint BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint BUFFERFLAGS_SILENT = 0x2;

    public const int E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int S_FALSE = 1;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct PROPERTYKEY {
    public Guid fmtid;
    public int pid;
    public PROPERTYKEY(Guid id, int p) { fmtid = id; pid = p; }
  }

  [StructLayout(LayoutKind.Explicit)]
  internal struct PROPVARIANT {
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
    [FieldOffset(8)] public uint uintValue;
  }

  /// <summary>
  /// WAVEFORMATEX plus the extensible tail. Declared as one flat struct with
  /// the union members appended, because every device that matters reports
  /// WAVE_FORMAT_EXTENSIBLE and reading cbSize then a second struct just moves
  /// the same bytes twice.
  /// </summary>
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  internal struct WAVEFORMATEXTENSIBLE {
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
  }

  internal static class WaveFormat {
    public const ushort PCM = 1;
    public const ushort IEEE_FLOAT = 3;
    public const ushort EXTENSIBLE = 0xFFFE;

    public static readonly Guid SUBTYPE_PCM =
      new Guid("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SUBTYPE_IEEE_FLOAT =
      new Guid("00000003-0000-0010-8000-00aa00389b71");
  }

  // -------------------------------------------------------------- interfaces

  /// <summary>CLSID_MMDeviceEnumerator. Not to be confused with the interface
  /// id below, which is a different GUID entirely.</summary>
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
  internal class MMDeviceEnumeratorComObject { }

  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
  }

  [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IMMDeviceCollection {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
  }

  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                               [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
  }

  [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IPropertyStore {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
  }

  [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioClient {
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
                                 long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
  }

  [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioCaptureClient {
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
  }

  [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionManager2 {
    // --- IAudioSessionManager ---
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IAudioSessionControl2 control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out ISimpleAudioVolume volume);
    // --- IAudioSessionManager2 ---
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    [PreserveSig] int RegisterSessionNotification(IntPtr notification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
    [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
  }

  [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionEnumerator {
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
  }

  [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IAudioSessionControl2 {
    // --- IAudioSessionControl ---
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    // --- IAudioSessionControl2 ---
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
  }

  [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface ISimpleAudioVolume {
    [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute(bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute(out bool mute);
  }

  /// <summary>Session states from AudioSessionState.</summary>
  internal static class SessionState {
    public const int Inactive = 0;
    public const int Active = 1;
    public const int Expired = 2;
  }

  internal static class Com {
    public const uint CLSCTX_ALL = 23;

    public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_ISimpleAudioVolume = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static readonly PROPERTYKEY PKEY_Device_FriendlyName =
      new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public const uint STGM_READ = 0;

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pv);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState,
                                            [MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;

    /// <summary>Release a COM object without caring whether it was ever set.</summary>
    public static void Release(object o) {
      try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
      catch (Exception) { }
    }

    public static bool Ok(int hr) { return hr >= 0; }
  }
}
