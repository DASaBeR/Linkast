// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// Camera discovery and shared capture sessions using WinRT <see cref="MediaCapture"/>.
/// Each physical device is opened at most once; every <see cref="ICameraFeed"/> is a reference to it.
/// </summary>
public sealed partial class MediaCaptureCameraService : ICameraService, IDisposable
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CameraSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private bool _enumerated;

    public MediaCaptureCameraService(ILogger<MediaCaptureCameraService>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler? DevicesChanged;

    /// <summary>Tests only: also list the Linkast Camera, to read back what Linkast publishes to it.</summary>
    public bool IncludeLinkastCamera { get; init; }

    /// <summary>Starts watching for cameras being plugged in or removed.</summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }

            _watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
            _watcher.Added += (_, _) => OnDevicesChanged();
            _watcher.Removed += (_, _) => OnDevicesChanged();
            _watcher.Updated += (_, _) => OnDevicesChanged();
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
        }
    }

    public async Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken).ConfigureAwait(false);
        // Linkast's own virtual camera is never offered as an input: it would show itself in a loop.
        return [.. devices
            .Where(d => d.IsEnabled && (IncludeLinkastCamera || !VirtualCameraProtocol.IsLinkastCamera(d.Name)))
            .Select(d => new CameraDevice(d.Id, d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        CameraSession? running;
        lock (_gate)
        {
            _sessions.TryGetValue(deviceId, out running);
        }

        if (running?.SupportedFormats is { Count: > 0 } known)
        {
            return known;
        }

        // Read formats without locking the device: shared read-only mode.
        var group = await CameraSession.FindSourceGroupAsync(deviceId).ConfigureAwait(false);
        if (group is null)
        {
            return [];
        }

        using var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            }).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }

        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        return source is null ? [] : CameraSession.ToFormats(source.SupportedFormats);
    }

    public ICameraFeed Open(CameraDevice device, CameraFormatRequest format)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);
        CameraSession session;
        lock (_gate)
        {
            var first = !_sessions.TryGetValue(device.Id, out session!);
            if (first)
            {
                session = new CameraSession(device, _logger);
                _sessions[device.Id] = session;
            }

            session.References++;
            session.Request(format, first);
            if (first)
            {
                // A previous session of this device may still be closing; open only after it has released the device.
                session.Start(_stopping.TryGetValue(device.Id, out var previous) ? previous : Task.CompletedTask);
            }
        }

        return new CameraFeed(this, session);
    }

    private async Task ReleaseAsync(CameraSession session)
    {
        Task stop;
        lock (_gate)
        {
            if (--session.References > 0)
            {
                return;
            }

            _sessions.Remove(session.Device.Id);
            stop = session.DisposeAsync().AsTask();
            _stopping[session.Device.Id] = stop;
        }

        await stop.ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopping.TryGetValue(session.Device.Id, out var current) && current == stop)
            {
                _stopping.Remove(session.Device.Id);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            {
                _watcher.Stop();
            }

            _watcher = null;
        }
    }

    /// <summary>Number of physical devices currently open. Used by tests to prove there are no leaks.</summary>
    public int OpenSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    private void OnDevicesChanged()
    {
        if (!_enumerated)
        {
            return;
        }

        CameraSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
        }

        foreach (var s in sessions)
        {
            s.NudgeReconnect();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One consumer's handle to a shared session.</summary>
    private sealed class CameraFeed(MediaCaptureCameraService owner, CameraSession session) : ICameraFeed
    {
        private int _disposed;

        public CameraDevice Device => session.Device;

        public CameraState State => session.State;

        public StreamError? LastError => session.LastError;

        public CameraFormat? ActiveFormat => session.ActiveFormat;

        public double MeasuredFps => session.MeasuredFps;

        public LatestFrameExchange Frames => session.Frames;

        public IReadOnlyList<HardwareControlInfo> HardwareControls => session.HardwareControls;

        public event EventHandler? StateChanged
        {
            add => session.StateChanged += value;
            remove => session.StateChanged -= value;
        }

        public Task<bool> SetHardwareControlAsync(HardwareControl control, double? value) => session.SetHardwareControlAsync(control, value);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.ReleaseAsync(session).ConfigureAwait(false);
            }
        }
    }
}
