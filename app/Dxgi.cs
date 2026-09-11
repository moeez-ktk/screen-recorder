using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LightRecorder {

  // ==========================================================================
  //  Just enough DXGI to answer one question correctly: which output index
  //  does ddagrab mean by "monitor 2"?
  //
  //  The Electron build guessed - it sorted Chromium's display list by
  //  position and used the array index, with a comment admitting it was "the
  //  right index in practice". It is not, on machines where the adapter
  //  enumerates its outputs in a different order from the desktop layout, and
  //  the symptom is recording the wrong screen. Asking DXGI directly is forty
  //  lines and always right.
  // ==========================================================================

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  internal struct DXGI_OUTPUT_DESC {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public Native.RECT DesktopCoordinates;
    [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
    public uint Rotation;
    public IntPtr Monitor;
  }

  [ComImport, Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IDXGIObject {
    [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
    [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
    [PreserveSig] int GetParent(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
  }

  [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IDXGIFactory1 {
    // --- IDXGIObject ---
    [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
    [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
    [PreserveSig] int GetParent(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
    // --- IDXGIFactory ---
    [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter1 adapter);
    [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
    [PreserveSig] int GetWindowAssociation(out IntPtr window);
    [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
    [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IDXGIAdapter1 adapter);
    // --- IDXGIFactory1 ---
    [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    [PreserveSig] int IsCurrent();
  }

  [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IDXGIAdapter1 {
    // --- IDXGIObject ---
    [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
    [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
    [PreserveSig] int GetParent(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
    // --- IDXGIAdapter ---
    [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    [PreserveSig] int GetDesc(IntPtr desc);
    [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long umdVersion);
    // --- IDXGIAdapter1 ---
    [PreserveSig] int GetDesc1(IntPtr desc);
  }

  [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IDXGIOutput {
    // --- IDXGIObject ---
    [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
    [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
    [PreserveSig] int GetParent(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
    // --- IDXGIOutput ---
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    // The rest of the interface is never called, so it is left undeclared -
    // GetDesc is the first slot and nothing below it is reachable from here.
  }

  internal static class Dxgi {

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>
    /// Device name (\\.\DISPLAY1) to the output index on the first adapter,
    /// which is the numbering ddagrab uses. Rebuilt on demand: monitors get
    /// unplugged, and this is only consulted when a recording starts or the
    /// picker opens.
    /// </summary>
    public static Dictionary<string, int> OutputIndexByDeviceName() {
      var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      object factoryObj = null;
      IDXGIFactory1 factory = null;

      try {
        Guid iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out factoryObj) < 0 || factoryObj == null) return map;
        factory = (IDXGIFactory1)factoryObj;

        // ddagrab, handed no device of its own, creates one on the first
        // adapter, so that is the one whose outputs we number.
        IDXGIAdapter1 adapter;
        if (factory.EnumAdapters1(0, out adapter) < 0 || adapter == null) return map;

        try {
          for (uint i = 0; i < 32; i++) {
            IDXGIOutput output;
            if (adapter.EnumOutputs(i, out output) < 0 || output == null) break;
            try {
              DXGI_OUTPUT_DESC desc;
              if (output.GetDesc(out desc) >= 0 && !string.IsNullOrEmpty(desc.DeviceName))
                map[desc.DeviceName] = (int)i;
            } finally {
              Com.Release(output);
            }
          }
        } finally {
          Com.Release(adapter);
        }
      } catch (Exception err) {
        Log.Warn("DXGI output enumeration failed: " + err.Message);
      } finally {
        Com.Release(factory);
      }

      return map;
    }
  }
}
