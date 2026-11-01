// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Low-level COM and WinRT interop helpers for Windows Graphics Capture and Direct3D 11.
/// </summary>
public static class WindowsGraphicsInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11Texture2DIid = typeof(ID3D11Texture2D).GUID;

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow([In] IntPtr hWnd, [In] ref Guid riid, [Out] out IntPtr result);

        [PreserveSig]
        int CreateForMonitor([In] IntPtr hMonitor, [In] ref Guid riid, [Out] out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface([In] ref Guid iid, [Out] out IntPtr p);
    }

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> targeting a specific top-level window.
    /// </summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle cannot be zero.", nameof(hWnd));
        }

        var interop = GetCaptureItemInterop();
        var iid = GraphicsCaptureItemIid;
        int hr = interop.CreateForWindow(hWnd, ref iid, out var rawItem);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(rawItem);
        }
        finally
        {
            Marshal.Release(rawItem);
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> targeting a specific display monitor.
    /// </summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            throw new ArgumentException("Monitor handle cannot be zero.", nameof(hMonitor));
        }

        var interop = GetCaptureItemInterop();
        var iid = GraphicsCaptureItemIid;
        int hr = interop.CreateForMonitor(hMonitor, ref iid, out var rawItem);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(rawItem);
        }
        finally
        {
            Marshal.Release(rawItem);
        }
    }

    /// <summary>
    /// Extracts the underlying Direct3D 11 texture from a WinRT <see cref="IDirect3DSurface"/>.
    /// Caller takes ownership of the returned <see cref="ID3D11Texture2D"/> and is responsible for disposing it.
    /// </summary>
    public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var rawSurfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessGuid = DxgiInterfaceAccessIid;
            int hrQi = Marshal.QueryInterface(rawSurfacePtr, in accessGuid, out var accessPtr);
            Marshal.ThrowExceptionForHR(hrQi);

            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                var texGuid = D3D11Texture2DIid;
                int hrGet = access.GetInterface(ref texGuid, out var texPtr);
                Marshal.ThrowExceptionForHR(hrGet);

                return new ID3D11Texture2D(texPtr);
            }
            finally
            {
                Marshal.Release(accessPtr);
            }
        }
        finally
        {
            Marshal.Release(rawSurfacePtr);
        }
    }

    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hrStr = WindowsCreateString(className, className.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hrStr);

        var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
        try
        {
            int hrFactory = RoGetActivationFactory(hstring, ref interopGuid, out var factoryPtr);
            Marshal.ThrowExceptionForHR(hrFactory);
            try
            {
                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }
}
