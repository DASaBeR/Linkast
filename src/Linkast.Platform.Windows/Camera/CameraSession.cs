// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Linkast.Core;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using WinRT;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// One physical camera, opened once and shared by all feeds. Runs a supervision loop that opens the
/// device, watches for failures and silent stalls, and reopens it when it returns.
/// </summary>
internal sealed partial class CameraSession : IAsyncDisposable
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _run;
    private CameraFormatRequest _request = CameraFormatRequest.Auto;
    private volatile bool _restartRequested;
    private TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private long _lastFrameTimestamp;
    private long _fpsWindowStart = Stopwatch.GetTimestamp();
    private int _fpsWindowFrames;

    public CameraSession(CameraDevice device, ILogger logger)
    {
        Device = device;
        _logger = logger;
    }

    public CameraDevice Device { get; }

    public CameraState State { get; private set; } = CameraState.Off;

    public StreamError? LastError { get; private set; }

    public CameraFormat? ActiveFormat { get; private set; }

    public IReadOnlyList<CameraFormat> SupportedFormats { get; private set; } = [];

    public IReadOnlyList<HardwareControlInfo> HardwareControls { get; private set; } = [];

    public double MeasuredFps { get; private set; }

    public LatestFrameExchange Frames { get; } = new();

    public event EventHandler? StateChanged;

    /// <summary>Number of feeds holding this session. Guarded by the owning service's lock.</summary>
    public int References { get; set; }

    /// <summary>Applies a format request. The last explicit request wins: one device runs one format.</summary>
    public void Request(CameraFormatRequest request, bool firstReference)
    {
        lock (_gate)
        {
            if (_request == request || (!firstReference && request.IsAuto))
            {
                return;
            }

            _request = request;
            if (!firstReference)
            {
                _restartRequested = true;
                _wake.Release();
            }
        }
    }

    /// <summary>Starts the supervision loop once <paramref name="previous"/> (an earlier session of the same device) has fully stopped.</summary>
    public void Start(Task previous)
    {
        lock (_gate)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(
                async () =>
                {
                    await previous.ConfigureAwait(false);
                    await RunAsync(token).ConfigureAwait(false);
                },
                CancellationToken.None);
        }
    }

    /// <summary>Stops capture and releases the device. Completes when the device is closed.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate)
        {
            _cts?.Cancel();
            _wake.Release();
            run = _run;
        }

        if (run is not null)
        {
            await run.ConfigureAwait(false);
        }

        _cts?.Dispose();
        _wake.Dispose();
    }

    /// <summary>Called when devices are added or removed, so a waiting session retries immediately.</summary>
    public void NudgeReconnect()
    {
        if (State is CameraState.Unavailable or CameraState.Error)
        {
            _wake.Release();
        }
    }

    public Task<bool> SetHardwareControlAsync(HardwareControl control, double? value)
    {
        var controller = _capture?.VideoDeviceController;
        if (controller is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            if (control == HardwareControl.Focus && controller.FocusControl.Supported && value is null)
            {
                controller.FocusControl.Configure(new FocusSettings { Mode = FocusMode.Continuous, AutoFocusRange = AutoFocusRange.FullRange });
                _ = controller.FocusControl.FocusAsync();
                return Task.FromResult(true);
            }

            var c = ControlFor(controller, control);
            if (c is null || !c.Capabilities.Supported)
            {
                return Task.FromResult(false);
            }

            if (value is null)
            {
                return Task.FromResult(c.Capabilities.AutoModeSupported && c.TrySetAuto(true));
            }

            if (c.Capabilities.AutoModeSupported)
            {
                c.TrySetAuto(false);
            }

            var v = Math.Clamp(value.Value, c.Capabilities.Min, c.Capabilities.Max);
            return Task.FromResult(c.TrySetValue(v));
        }
        catch (COMException)
        {
            return Task.FromResult(false);
        }
    }

    internal static async Task<MediaFrameSourceGroup?> FindSourceGroupAsync(string deviceId)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups.FirstOrDefault(g => g.SourceInfos.Any(s =>
            s.SourceKind == MediaFrameSourceKind.Color
            && string.Equals(s.DeviceInformation?.Id, deviceId, StringComparison.OrdinalIgnoreCase)));
    }

    internal static IReadOnlyList<CameraFormat> ToFormats(IEnumerable<MediaFrameFormat> formats) =>
    [
        .. formats
            .Where(f => f.VideoFormat is not null && f.FrameRate is not null && f.VideoFormat.Width > 0)
            .Select(f => new CameraFormat((int)f.VideoFormat.Width, (int)f.VideoFormat.Height, f.FrameRate.Numerator, f.FrameRate.Denominator, f.Subtype))
            .Distinct()
            .OrderByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.FrameRate),
    ];

    private async Task RunAsync(CancellationToken ct)
    {
        SetState(CameraState.Starting, null);
        while (!ct.IsCancellationRequested)
        {
            var retry = true;
            try
            {
                await OpenAsync(ct).ConfigureAwait(false);
                await MonitorAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CameraOpenException ex)
            {
                LogOpenFailed(Device.Name, ex.Error.Code, ex.Error.Detail);
                SetState(ex.Fatal ? CameraState.Error : CameraState.Unavailable, ex.Error);
                retry = true;
            }
#pragma warning disable CA1031 // The supervisor must survive anything the device throws.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogOpenFailed(Device.Name, "camera.unexpected", ex.Message);
                SetState(CameraState.Unavailable, new StreamError(ErrorCategory.Capture, "camera.unexpected", $"'{Device.Name}' stopped unexpectedly. Retrying…", ex.Message));
            }
            finally
            {
                await CloseAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested || !retry)
            {
                break;
            }

            if (_restartRequested)
            {
                _restartRequested = false;
                continue; // format change: reopen immediately
            }

            try
            {
                await _wake.WaitAsync(State == CameraState.Error ? RetryDelay * 3 : RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (State is CameraState.Unavailable or CameraState.Error)
            {
                SetState(CameraState.Starting, LastError);
            }
        }

        Frames.Clear();
        SetState(CameraState.Off, null);
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        var group = await FindSourceGroupAsync(Device.Id).ConfigureAwait(false)
            ?? throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.disconnected",
                $"'{Device.Name}' is not connected. Linkast will use it as soon as it returns."), fatal: false);

        _failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = false;
        _capture = new MediaCapture();
        _capture.Failed += (_, e) => _failed.TrySetResult();
        try
        {
            await _capture.InitializeAsync(Settings(group, MediaCaptureSharingMode.ExclusiveControl)).AsTask(ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.access_denied",
                "Windows is blocking camera access. Open Settings > Privacy & security > Camera and allow desktop apps to use the camera.", ex.Message), fatal: true);
        }
        catch (COMException)
        {
            // Another app has exclusive control. Shared read-only still delivers frames,
            // but the format and hardware controls are locked by the other app.
            _capture.Dispose();
            _capture = new MediaCapture();
            _capture.Failed += (_, e) => _failed.TrySetResult();
            try
            {
                await _capture.InitializeAsync(Settings(group, MediaCaptureSharingMode.SharedReadOnly)).AsTask(ct).ConfigureAwait(false);
                shared = true;
            }
            catch (Exception ex2) when (ex2 is COMException or UnauthorizedAccessException)
            {
                throw InUse(ex2);
            }
        }

        var source = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.no_video", $"'{Device.Name}' does not provide a colour video stream."), fatal: true);

        SupportedFormats = ToFormats(source.SupportedFormats);
        if (!shared)
        {
            var wanted = _request.Choose(SupportedFormats);
            var match = wanted is null ? null : source.SupportedFormats.FirstOrDefault(f =>
                f.VideoFormat.Width == wanted.Width && f.VideoFormat.Height == wanted.Height
                && f.FrameRate.Numerator == wanted.FrameRateNumerator && f.FrameRate.Denominator == wanted.FrameRateDenominator
                && f.Subtype == wanted.Subtype);
            if (match is not null)
            {
                try
                {
                    await source.SetFormatAsync(match).AsTask(ct).ConfigureAwait(false);
                }
                catch (COMException ex)
                {
                    LogOpenFailed(Device.Name, "camera.format", ex.Message);
                }
            }
        }

        var current = source.CurrentFormat;
        ActiveFormat = new CameraFormat((int)current.VideoFormat.Width, (int)current.VideoFormat.Height, current.FrameRate.Numerator, current.FrameRate.Denominator, current.Subtype);
        HardwareControls = shared ? [] : ReadControls(_capture.VideoDeviceController);

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(ct).ConfigureAwait(false);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        Volatile.Write(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());
        var status = await _reader.StartAsync().AsTask(ct).ConfigureAwait(false);
        switch (status)
        {
            case MediaFrameReaderStartStatus.Success:
                break;
            case MediaFrameReaderStartStatus.ExclusiveControlNotAvailable:
                throw InUse(null);
            case MediaFrameReaderStartStatus.DeviceNotAvailable:
                throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.disconnected", $"'{Device.Name}' is not available right now. Retrying…"), fatal: false);
            default:
                throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.format_unsupported",
                    $"'{Device.Name}' could not start in {ActiveFormat}. Choose another resolution or Automatic.", status.ToString()), fatal: true);
        }

        LogOpened(Device.Name, ActiveFormat.ToString(), shared);
        SetState(CameraState.Running, shared
            ? new StreamError(ErrorCategory.Capture, "camera.shared", "Another app controls this camera, so its resolution and hardware settings are locked. The picture still works.")
            : null);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_restartRequested)
        {
            var wakeTask = _wake.WaitAsync(TimeSpan.FromSeconds(1), ct);
            var done = await Task.WhenAny(_failed.Task, wakeTask).ConfigureAwait(false);
            if (done == _failed.Task)
            {
                throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.lost", $"'{Device.Name}' stopped working or was unplugged. Retrying…"), fatal: false);
            }

            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFrameTimestamp)) > StallTimeout)
            {
                throw new CameraOpenException(new StreamError(ErrorCategory.Capture, "camera.stalled", $"'{Device.Name}' stopped sending pictures. Retrying…"), fatal: false);
            }
        }
    }

    private async Task CloseAsync()
    {
        var reader = Interlocked.Exchange(ref _reader, null);
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch (COMException)
            {
                // Device already gone.
            }

            reader.Dispose();
        }

        Interlocked.Exchange(ref _capture, null)?.Dispose();
        HardwareControls = [];
        MeasuredFps = 0;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            bitmap?.Dispose();
            return;
        }

        using (bitmap)
        {
            var target = Frames.BeginWrite(bitmap.PixelWidth, bitmap.PixelHeight);
            if (target is not null)
            {
                CopyPixels(bitmap, target);
                Frames.Publish(frame!.SystemRelativeTime ?? TimeSpan.Zero);
            }
        }

        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastFrameTimestamp, now);
        _fpsWindowFrames++;
        var elapsed = Stopwatch.GetElapsedTime(_fpsWindowStart, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            MeasuredFps = _fpsWindowFrames / elapsed.TotalSeconds;
            _fpsWindowFrames = 0;
            _fpsWindowStart = now;
        }
    }

    private static unsafe void CopyPixels(SoftwareBitmap bitmap, BgraFrame target)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out _);
        var rowBytes = target.Width * 4;
        fixed (byte* dst = target.Pixels)
        {
            for (var y = 0; y < target.Height; y++)
            {
                Buffer.MemoryCopy(data + plane.StartIndex + ((long)y * plane.Stride), dst + ((long)y * rowBytes), rowBytes, rowBytes);
            }
        }
    }

    private static List<HardwareControlInfo> ReadControls(VideoDeviceController c)
    {
        var list = new List<HardwareControlInfo>();
        foreach (var kind in Enum.GetValues<HardwareControl>())
        {
            var control = ControlFor(c, kind);
            if (control is null)
            {
                continue;
            }

            var caps = control.Capabilities;
            list.Add(new HardwareControlInfo(kind, caps.Supported, caps.Min, caps.Max, caps.Step, caps.Default, caps.AutoModeSupported));
        }

        return list;
    }

    private static MediaDeviceControl? ControlFor(VideoDeviceController c, HardwareControl control) => control switch
    {
        HardwareControl.Exposure => c.Exposure,
        HardwareControl.Focus => c.Focus,
        HardwareControl.WhiteBalance => c.WhiteBalance,
        HardwareControl.Brightness => c.Brightness,
        HardwareControl.Contrast => c.Contrast,
        _ => null,
    };

    private static MediaCaptureInitializationSettings Settings(MediaFrameSourceGroup group, MediaCaptureSharingMode mode) => new()
    {
        SourceGroup = group,
        SharingMode = mode,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        StreamingCaptureMode = StreamingCaptureMode.Video,
    };

    private CameraOpenException InUse(Exception? inner) => new(new StreamError(ErrorCategory.Capture, "camera.in_use",
        $"'{Device.Name}' is being used by another app (for example Teams, Zoom or Discord). Close it there, and Linkast will pick it up automatically.",
        inner?.Message), fatal: false);

    private void SetState(CameraState state, StreamError? error)
    {
        var changed = State != state || LastError != error;
        State = state;
        LastError = error;
        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera '{Name}' opened: {Format} (shared: {Shared})")]
    private partial void LogOpened(string name, string format, bool shared);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera '{Name}': {Code} {Detail}")]
    private partial void LogOpenFailed(string name, string code, string? detail);

    private sealed class CameraOpenException(StreamError error, bool fatal) : Exception(error.UserMessage)
    {
        public StreamError Error { get; } = error;

        public bool Fatal { get; } = fatal;
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
