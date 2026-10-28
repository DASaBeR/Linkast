// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI.Composition;
using WinRT;

namespace Linkast.App.Services;

/// <summary>
/// Renders a web page (alerts, a chat box) off screen with a transparent background and keeps its
/// latest picture as straight-alpha BGRA. WebView2 draws into a composition visual that Windows
/// Graphics Capture reads on the GPU; no window is shown. Create and dispose on the UI thread.
/// </summary>
public sealed class WebOverlayRenderer : IAsyncDisposable
{
    /// <summary>Frames are converted at most this often; alerts don't need more and it halves the CPU.</summary>
    private const int MaxFramesPerSecond = 30;

    private static Window? s_host;

    // One environment (so one browser process) per overlay: each overlay's sound can then be captured
    // on its own, from that process, for its own fader in the mixer.
    private static readonly Dictionary<string, CoreWebView2Environment> s_environments = new(StringComparer.Ordinal);

    private readonly CoreWebView2CompositionController _controller;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Lock _gate = new();
    private readonly long _minFrameTicks = Stopwatch.Frequency / MaxFramesPerSecond;
    private ID3D11Texture2D? _staging;
    private byte[] _latest;
    private byte[] _work;
    private bool _hasFrame;
    private long _lastFrame;
    private bool _disposed;

    private WebOverlayRenderer(CoreWebView2CompositionController controller, ID3D11Device device, Direct3D11CaptureFramePool pool,
        GraphicsCaptureSession session, int width, int height)
    {
        _controller = controller;
        _device = device;
        _context = device.ImmediateContext;
        _pool = pool;
        _session = session;
        Width = width;
        Height = height;
        _latest = new byte[width * height * 4];
        _work = new byte[width * height * 4];
        _pool.FrameArrived += OnFrameArrived;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Set when the page could not be loaded (shown next to the overlay).</summary>
    public string? Problem { get; private set; }

    /// <summary>The overlay's own browser process (its sound is captured from it), or 0 if unknown.</summary>
    public uint ProcessId
    {
        get
        {
            try
            {
                return _disposed ? 0 : _controller.CoreWebView2.BrowserProcessId;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return 0; // the browser process went away
            }
        }
    }

    /// <summary>
    /// Loads <paramref name="url"/> at <paramref name="width"/> × <paramref name="height"/> CSS pixels in the
    /// overlay's own browser (<paramref name="layerId"/>). UI thread only.
    /// </summary>
    public static async Task<WebOverlayRenderer> CreateAsync(string layerId, string url, int width, int height)
    {
        EnsureDispatcherQueue();
        s_host ??= new Window { Width = 1, Height = 1, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false };
        var hwnd = new WindowInteropHelper(s_host).EnsureHandle();

        if (!s_environments.TryGetValue(layerId, out var environment))
        {
            // A data folder of its own is what gives the overlay a browser process of its own.
            var folder = Path.Combine(App.DataDirectory, "WebView2", string.Concat(layerId.Where(char.IsAsciiLetterOrDigit)));
            environment = await CoreWebView2Environment.CreateAsync(null, folder,
                new CoreWebView2EnvironmentOptions(
                    // Alerts play their sound without a click, and a page that is never on screen keeps
                    // running at full speed (the same choices OBS makes for its browser source).
                    "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows"));
            s_environments[layerId] = environment;
        }

        var compositor = new Compositor();
        var root = compositor.CreateContainerVisual();
        root.Size = new Vector2(width, height);

        var controller = await environment.CreateCoreWebView2CompositionControllerAsync(hwnd);
        controller.RootVisualTarget = root;
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.RasterizationScale = 1.0;
        controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        controller.IsVisible = true;
        var settings = controller.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device? device).CheckError();
        using var dxgi = device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        var item = GraphicsCaptureItem.CreateFromVisual(root);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        var renderer = new WebOverlayRenderer(controller, device, pool, session, width, height);

        controller.CoreWebView2.NavigationCompleted += (_, e) =>
            renderer.Problem = e.IsSuccess ? null : $"The page did not load ({e.WebErrorStatus}). Check the link and your connection.";
        controller.CoreWebView2.Navigate(url);
        session.StartCapture();
        return renderer;
    }

    /// <summary>Copies the latest picture into <paramref name="destination"/>. False until the page has drawn.</summary>
    public bool CopyLatest(byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            if (!_hasFrame || destination.Length != _latest.Length)
            {
                return false;
            }

            Buffer.BlockCopy(_latest, 0, destination, 0, _latest.Length);
            return true;
        }
    }

    /// <summary>The latest picture as an image for the preview, or null before the page has drawn.</summary>
    public BitmapSource? Snapshot()
    {
        var copy = new byte[Width * Height * 4];
        if (!CopyLatest(copy))
        {
            return null;
        }

        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, copy, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Loads the page again, for example after changing the alert design. UI thread only.</summary>
    public void Reload() => _controller.CoreWebView2.Reload();

    /// <summary>
    /// Plays a short two-note chime inside the page, at about the loudness of a typical alert (−6 dBFS
    /// peak). It comes out of the overlay's own browser, so it is heard, metered and streamed exactly like
    /// a real alert sound, through the overlay's fader.
    /// </summary>
    public Task PlayTestSoundAsync() => _controller.CoreWebView2.ExecuteScriptAsync(TestSoundScript);

    private const string TestSoundScript = """
        (() => {
          const ctx = new AudioContext();
          const out = ctx.createGain();
          out.gain.value = 0.5;
          out.connect(ctx.destination);
          [[880, 0], [1320, 0.35]].forEach(([freq, at]) => {
            const osc = ctx.createOscillator();
            const env = ctx.createGain();
            osc.frequency.value = freq;
            const t = ctx.currentTime + at;
            env.gain.setValueAtTime(0, t);
            env.gain.linearRampToValueAtTime(1, t + 0.02);
            env.gain.exponentialRampToValueAtTime(0.001, t + 0.9);
            osc.connect(env).connect(out);
            osc.start(t);
            osc.stop(t + 0.95);
          });
          setTimeout(() => ctx.close(), 2000);
        })();
        """;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _controller.Close();
        lock (_gate)
        {
            _staging?.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        var now = Stopwatch.GetTimestamp();
        if (frame is null || now - Volatile.Read(ref _lastFrame) < _minFrameTicks)
        {
            return;
        }

        Volatile.Write(ref _lastFrame, now);
        using var texture = TextureOf(frame.Surface);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReadBack(texture);
        }

        // Swap so readers always get a complete frame.
        lock (_gate)
        {
            (_latest, _work) = (_work, _latest);
            _hasFrame = true;
        }
    }

    /// <summary>GPU texture → <see cref="_work"/>, converted from premultiplied to straight alpha.</summary>
    private void ReadBack(ID3D11Texture2D texture)
    {
        if (_staging is null)
        {
            var d = texture.Description;
            d.Usage = ResourceUsage.Staging;
            d.BindFlags = BindFlags.None;
            d.CPUAccessFlags = CpuAccessFlags.Read;
            d.MiscFlags = ResourceOptionFlags.None;
            _staging = _device.CreateTexture2D(d);
        }

        _context.CopyResource(_staging, texture);
        var map = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            var w = Math.Min(Width, (int)texture.Description.Width);
            var h = Math.Min(Height, (int)texture.Description.Height);
            var row = w * 4;
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(map.DataPointer + (y * (nint)map.RowPitch), _work, y * Width * 4, row);
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        Unpremultiply(_work);
    }

    private static void Unpremultiply(Span<byte> bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a is 0 or 255)
            {
                continue;
            }

            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
    }

    private static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessIid, out var accessPtr));
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            var textureIid = typeof(ID3D11Texture2D).GUID;
            return new ID3D11Texture2D(access.GetInterface(ref textureIid));
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }

    /// <summary>Windows.UI.Composition needs a DispatcherQueue on the UI thread; created once.</summary>
    private static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
