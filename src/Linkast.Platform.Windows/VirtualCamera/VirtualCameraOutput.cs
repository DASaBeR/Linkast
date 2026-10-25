// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.MemoryMappedFiles;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Camera;

namespace Linkast.Platform.Windows.VirtualCamera;

public enum VirtualCameraState
{
    /// <summary>Turned off in Linkast.</summary>
    Off,

    /// <summary>On, but no app is using the Linkast Camera right now (nothing is rendered).</summary>
    Waiting,

    /// <summary>An app (Zoom, Teams, …) is showing the Linkast Camera.</summary>
    Serving,
}

/// <summary>
/// Feeds the Linkast Camera. While an app is using it, renders the chosen camera layer, with its
/// background effect and auto-framing, at 1280 x 720 and publishes it to the media source's shared
/// section. Costs nothing while no app is watching. Uses the same shared camera session and effect
/// workers as the preview and the stream, so the camera is never opened twice.
/// </summary>
public sealed class VirtualCameraOutput : IAsyncDisposable
{
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReleaseAfterIdle = TimeSpan.FromSeconds(30);

    private readonly ICameraService _cameras;
    private readonly ISegmentationService? _segmentation;
    private readonly BackgroundImageCache? _images;
    private readonly IFramingService? _framing;
    private readonly string _sectionName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private LiveCameraLayer? _layer;
    private byte[]? _standby;
    private volatile bool _enabled;

    public VirtualCameraOutput(ICameraService cameras, ISegmentationService? segmentation = null, BackgroundImageCache? images = null,
        IFramingService? framing = null, string sectionName = VirtualCameraProtocol.SectionName)
    {
        _cameras = cameras;
        _segmentation = segmentation;
        _images = images;
        _framing = framing;
        _sectionName = sectionName;
        _loop = Task.Factory.StartNew(() => RunAsync(_cts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public VirtualCameraState State { get; private set; } = VirtualCameraState.Off;

    /// <summary>
    /// True when an app opened a Linkast Camera installed by a different Linkast version (another
    /// shared-memory protocol). "Repair or update" installs the matching component.
    /// </summary>
    public bool NeedsComponentUpdate { get; private set; }

    /// <summary>Frames delivered to camera apps (reported by the media source).</summary>
    public long FramesDelivered { get; private set; }

    /// <summary>Raised on a worker thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The camera layer to show. Null shows the standby picture.</summary>
    public LiveCameraLayer? Layer
    {
        get => Volatile.Read(ref _layer);
        set => Volatile.Write(ref _layer, value);
    }

    /// <summary>1280 x 720 BGRA picture shown while the chosen camera is off or unavailable.</summary>
    public void SetStandbyFrame(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != VirtualCameraProtocol.FrameBytes)
        {
            throw new ArgumentException("The standby picture must be 1280 x 720 BGRA.", nameof(bgra));
        }

        Volatile.Write(ref _standby, bgra);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private void SetState(VirtualCameraState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[VirtualCameraProtocol.FrameBytes];
        var renderer = new CameraLayerRenderer();
        MemoryMappedFile? section = null;
        MemoryMappedViewAccessor? view = null;
        ICameraFeed? feed = null;
        (string Device, CameraFormatRequest Format)? feedKey = null;
        await using var effects = new LayerEffectsBinding(_segmentation, _images, _framing);
        var lastProbe = DateTime.MinValue;
        DateTime? idleSince = null;
        long lastSequence = 0;
        (long Frame, CameraLayerSettings Settings, SegmentationMask? Mask, FramingCrop? Framing)? lastRender = null;

        async Task CloseFeedAsync()
        {
            if (feed is not null)
            {
                await feed.DisposeAsync().ConfigureAwait(false);
                feed = null;
                feedKey = null;
            }
        }

        void CloseSection()
        {
            view?.Dispose();
            section?.Dispose();
            view = null;
            section = null;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / VirtualCameraProtocol.FrameRate));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                if (!_enabled)
                {
                    await CloseFeedAsync().ConfigureAwait(false);
                    CloseSection();
                    SetState(VirtualCameraState.Off);
                    continue;
                }

                // The media source creates the section when an app opens the Linkast Camera.
                if (view is null)
                {
                    SetState(VirtualCameraState.Waiting);
                    if (now - lastProbe < ProbeEvery)
                    {
                        continue;
                    }

                    lastProbe = now;
                    try
                    {
                        section = MemoryMappedFile.OpenExisting(_sectionName, MemoryMappedFileRights.ReadWrite);
                        view = section.CreateViewAccessor(0, VirtualCameraProtocol.SectionSize, MemoryMappedFileAccess.ReadWrite);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
                    {
                        CloseSection();
                        continue;
                    }
                }

                var active = WithSection(view, span =>
                {
                    if (!VirtualCameraProtocol.IsCompatible(span))
                    {
                        return (bool?)null;
                    }

                    FramesDelivered = VirtualCameraProtocol.ConsumerFrames(span);
                    lastSequence = VirtualCameraProtocol.ProducerSequence(span);
                    return VirtualCameraProtocol.IsConsumerActive(span, now);
                });

                if (active is null)
                {
                    // An older or newer media source: Linkast offers an update in the UI.
                    CloseSection();
                    if (!NeedsComponentUpdate)
                    {
                        NeedsComponentUpdate = true;
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }

                    continue;
                }

                NeedsComponentUpdate = false;

                if (active == false)
                {
                    // Nobody is watching: don't render. Keep the camera open briefly in case the app
                    // just switched resolution, then release everything.
                    idleSince ??= now;
                    if (now - idleSince > TimeSpan.FromSeconds(3))
                    {
                        await CloseFeedAsync().ConfigureAwait(false);
                    }

                    if (now - idleSince > ReleaseAfterIdle)
                    {
                        CloseSection();
                    }

                    SetState(VirtualCameraState.Waiting);
                    continue;
                }

                idleSince = null;
                SetState(VirtualCameraState.Serving);

                // Keep the camera feed in line with the chosen layer (shared session: no second open).
                var layer = Layer;
                var device = layer is { Enabled: true } ? layer.Device : null;
                var wanted = device is null ? ((string, CameraFormatRequest)?)null : (device.Id, layer!.Format);
                if (wanted != feedKey)
                {
                    await CloseFeedAsync().ConfigureAwait(false);
                    if (wanted is not null)
                    {
                        feed = _cameras.Open(device!, layer!.Format);
                        feedKey = wanted;
                    }
                }

                var rendered = false;
                if (layer is not null && feed is { State: CameraState.Running })
                {
                    var fx = effects.Update(layer);
                    var settings = layer.Settings;
                    var frame = feed.Frames.TryBeginRead(0);
                    if (frame is not null)
                    {
                        try
                        {
                            // Cameras often deliver fewer frames than the 30 the camera apps pull: only
                            // render when the picture can have changed, otherwise resend the last one.
                            var key = (frame.Sequence, settings, fx.Mask, fx.Framing);
                            if (key != lastRender)
                            {
                                renderer.Render(frame, VirtualCameraProtocol.ForVirtualCamera(settings), buffer,
                                    VirtualCameraProtocol.Width, VirtualCameraProtocol.Height, fx.Mask, fx.BackgroundImage, FrameContext.Still, fx.Framing);
                                VirtualCameraProtocol.FlattenOnto(buffer, 0x1C, 0x0A, 0x09);
                                lastRender = key;
                            }

                            rendered = true;
                        }
                        finally
                        {
                            feed.Frames.EndRead(frame);
                        }
                    }
                }

                if (!rendered)
                {
                    lastRender = null;
                }

                var source = rendered ? buffer : Volatile.Read(ref _standby);
                if (source is not null)
                {
                    WithSection(view, span => VirtualCameraProtocol.Publish(span, source, DateTime.UtcNow));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await CloseFeedAsync().ConfigureAwait(false);
            CloseSection();
            SetState(VirtualCameraState.Off);
        }
    }

    private static unsafe T WithSection<T>(MemoryMappedViewAccessor view, SpanFunc<T> action)
    {
        byte* pointer = null;
        var handle = view.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref pointer);
        try
        {
            return action(new Span<byte>(pointer + view.PointerOffset, VirtualCameraProtocol.SectionSize));
        }
        finally
        {
            handle.ReleasePointer();
        }
    }

    private delegate T SpanFunc<out T>(Span<byte> section);
}
