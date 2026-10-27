// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Linkast.Platform.Windows.Audio;

/// <summary>Receives captured audio on the capture thread. The span is only valid during the call.</summary>
public delegate void AudioDataHandler(ReadOnlySpan<byte> data, bool silent);

/// <summary>One listener's connection to a shared capture session.</summary>
public sealed class AudioTap : IDisposable
{
    private readonly AudioCaptureHub.Session _session;
    private readonly AudioDataHandler _handler;
    private int _disposed;

    internal AudioTap(AudioCaptureHub.Session session, AudioDataHandler handler)
    {
        _session = session;
        _handler = handler;
    }

    public PcmFormat Format => _session.Format;

    public string DeviceName => _session.DeviceName;

    /// <summary>Raised (on a capture thread) when the device fails or disappears.</summary>
    public event EventHandler<Exception>? Failed;

    internal void Deliver(ReadOnlySpan<byte> data, bool silent) => _handler(data, silent);

    internal void Fail(Exception ex) => Failed?.Invoke(this, ex);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.Owner.Release(_session, this);
        }
    }
}

/// <summary>
/// Opens each audio endpoint at most once and shares it: the stream's audio input and the level meter
/// for audio-reactive effects listen to the same capture session, so the microphone is never opened
/// twice. Listeners only read the data; nothing they do changes the audio that is streamed.
/// Every session delivers 48 kHz float stereo (converted by the Windows audio engine).
/// </summary>
public sealed class AudioCaptureHub
{
    private readonly Lock _gate = new();

    // Keyed by device ID and loopback, or for a program's sound by "process:{id}:{include|exclude}".
    private readonly Dictionary<(string DeviceId, bool Loopback), Session> _sessions = [];

    /// <summary>Endpoints currently being captured. Diagnostics and tests.</summary>
    public int ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Starts listening to a device (null = the Windows default). Opens the device only if no one else
    /// is listening to it already. Throws <see cref="StreamFailureException"/> if it can't be opened.
    /// </summary>
    public AudioTap Subscribe(string? deviceId, bool loopback, AudioDataHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = deviceId ?? DefaultDeviceId(loopback);
        lock (_gate)
        {
            if (!_sessions.TryGetValue((id, loopback), out var session))
            {
                session = Session.Open(this, id, loopback);
                _sessions[(id, loopback)] = session;
            }

            var tap = new AudioTap(session, handler);
            session.Add(tap);
            return tap;
        }
    }

    /// <summary>
    /// Starts listening to the sound of one program and its child processes (<paramref name="include"/>),
    /// or to everything that plays except that program and its children. Needs Windows 10 2004 or later.
    /// Shared like devices: the stream and the mixer's meter use the same capture.
    /// </summary>
    public AudioTap SubscribeProcess(uint processId, bool include, AudioDataHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var key = ($"process:{processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{(include ? "include" : "exclude")}", true);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                session = Session.OpenProcess(this, key.Item1, processId, include);
                _sessions[key] = session;
            }

            var tap = new AudioTap(session, handler);
            session.Add(tap);
            return tap;
        }
    }

    internal void Release(Session session, AudioTap tap)
    {
        lock (_gate)
        {
            if (session.Remove(tap) > 0)
            {
                return;
            }

            _sessions.Remove((session.DeviceId, session.Loopback));
        }

        session.Close();
    }

    private static string DefaultDeviceId(bool loopback)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var d = enumerator.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Console);
            return d.ID;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Audio, "audio.no_default",
                loopback ? "No audio output device was found for system audio." : "No microphone was found.", ex.Message), ex);
        }
    }

    internal sealed class Session
    {
        private readonly Lock _tapsGate = new();
        private readonly MMDevice? _device;
        private readonly WasapiRecorder _recorder;
        private AudioTap[] _taps = [];

        private Session(AudioCaptureHub owner, string deviceId, bool loopback, MMDevice? device, string deviceName, WasapiRecorder recorder)
        {
            Owner = owner;
            DeviceId = deviceId;
            Loopback = loopback;
            _device = device;
            _recorder = recorder;
            DeviceName = deviceName;
            Format = WasapiAudioInput.ToPcmFormat(recorder.WaveFormat);
        }

        public AudioCaptureHub Owner { get; }

        public string DeviceId { get; }

        public bool Loopback { get; }

        public string DeviceName { get; }

        public PcmFormat Format { get; }

        public static Session Open(AudioCaptureHub owner, string deviceId, bool loopback)
        {
            MMDevice? device = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId);

                // One fixed format from the shared-mode engine, whatever the device's own format is.
                var builder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithBufferLength(20)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
                if (loopback)
                {
                    builder = builder.WithLoopbackCapture();
                }

                var recorder = builder.Build();
                var session = new Session(owner, deviceId, loopback, device, device.FriendlyName, recorder);
                recorder.DataAvailable += session.OnData;
                recorder.RecordingStopped += session.OnStopped;
                recorder.StartRecording();
                return session;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                device?.Dispose();
                throw new StreamFailureException(new StreamError(
                    ErrorCategory.Audio, "audio.open", $"The audio device could not be opened. It may be unplugged or in use.", ex.Message), ex);
            }
        }

        /// <summary>A program's sound (or everything but it), through Windows' process loopback.</summary>
        public static Session OpenProcess(AudioCaptureHub owner, string key, uint processId, bool include)
        {
            try
            {
                // Process loopback has no mix format of its own, so the format must be given. Activation is
                // asynchronous; it runs off the caller's thread so a UI caller can't deadlock on it.
                var builder = new WasapiRecorderBuilder()
                    .WithProcessLoopback(processId, include ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithBufferLength(20)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
                var recorder = Task.Run(builder.BuildAsync).GetAwaiter().GetResult();
                var session = new Session(owner, key, loopback: true, device: null, include ? "App audio" : "Desktop audio", recorder);
                recorder.DataAvailable += session.OnData;
                recorder.RecordingStopped += session.OnStopped;
                recorder.StartRecording();
                return session;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
            {
                throw new StreamFailureException(new StreamError(
                    ErrorCategory.Audio, "audio.process", "A program's sound could not be captured. This needs Windows 10 version 2004 or later.", ex.Message), ex);
            }
        }

        public void Add(AudioTap tap)
        {
            lock (_tapsGate)
            {
                _taps = [.. _taps, tap];
            }
        }

        /// <summary>Removes a listener and returns how many remain.</summary>
        public int Remove(AudioTap tap)
        {
            lock (_tapsGate)
            {
                _taps = [.. _taps.Where(t => !ReferenceEquals(t, tap))];
                return _taps.Length;
            }
        }

        public void Close()
        {
            _recorder.DataAvailable -= OnData;
            _recorder.RecordingStopped -= OnStopped;
            try
            {
                _recorder.StopRecording();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Device already gone.
            }

            _recorder.Dispose();
            _device?.Dispose();
        }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            var silent = (flags & AudioClientBufferFlags.Silent) != 0;
            foreach (var tap in Volatile.Read(ref _taps))
            {
                tap.Deliver(buffer, silent);
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is null)
            {
                return;
            }

            foreach (var tap in Volatile.Read(ref _taps))
            {
                tap.Fail(e.Exception);
            }
        }
    }
}
