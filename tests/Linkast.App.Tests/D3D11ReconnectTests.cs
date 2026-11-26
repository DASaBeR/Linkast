// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Platform.Windows.Rendering;
using Microsoft.Extensions.Logging;

namespace Linkast.App.Tests;

/// <summary>
/// An automatic reconnect with a real FFmpeg and a real (local) RTMP server that drops the first connection.
/// A tester's log showed every reconnect cut again 11 s in as "stalled" (the new FFmpeg's frame count was
/// compared with the previous one's) and the live layout reset to the session's.
/// </summary>
public sealed class D3D11ReconnectTests
{
    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }

    /// <summary>Each connection is a new FFmpeg RTMP server on its own port; <see cref="DropCurrent"/> ends it.</summary>
    private sealed class LocalRtmpTransport(string ffmpeg) : IOutputTransport, IDisposable
    {
        private readonly List<Process> _servers = [];

        public int Connections => _servers.Count;

        public async Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var server = Process.Start(new ProcessStartInfo(ffmpeg,
                $"-hide_banner -loglevel error -listen 1 -f flv -i rtmp://127.0.0.1:{port}/live/test -c copy -f null -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            })!;
            server.ErrorDataReceived += (_, _) => { };
            server.BeginErrorReadLine();
            lock (_servers)
            {
                _servers.Add(server);
            }

            await Task.Delay(1500, cancellationToken); // let it listen before the sender connects
            return new Lease($"rtmp://127.0.0.1:{port}/live/test");
        }

        public void DropCurrent()
        {
            lock (_servers)
            {
                _servers[^1].Kill(entireProcessTree: true);
            }
        }

        public void Dispose()
        {
            lock (_servers)
            {
                foreach (var s in _servers)
                {
                    try
                    {
                        s.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    s.Dispose();
                }
            }
        }

        private sealed class Lease(string url) : IOutputLease
        {
            public string PublishUrl => url;
            public string TcUrl => string.Empty;
            public bool IsMeasured => false;
            public Task Completion { get; } = new TaskCompletionSource().Task;
            public NetworkSnapshot Snapshot() => new(0, 0, 0, null, url, null, true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [SkippableFact]
    public async Task A_reconnect_stays_live_and_keeps_the_live_layout()
    {
        FfmpegInstallation? ffmpeg = null;
        try { ffmpeg = await FfmpegLocator.LocateAsync(); } catch (StreamFailureException) { }
        Skip.If(ffmpeg is null, "FFmpeg is not available on this machine.");

        using var deviceManager = new D3D11DeviceManager();
        using var transport = new LocalRtmpTransport(ffmpeg!.Path);
        var log = new CaptureLogger();
        await using var engine = new D3D11StreamEngine(
            ffmpeg,
            transport,
            logger: log,
            deviceManager: deviceManager,
            // A quick tick reads the progress before the new FFmpeg has printed any, as in the tester's log.
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(15), MinBytesForLive = 0, TelemetryInterval = TimeSpan.FromMilliseconds(250) });

        var session = new StreamSession(
            Video: new BlankCanvasSource(),
            Audio: [],
            Encoder: new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            Destination: new Destination("Local", new Uri("rtmp://127.0.0.1/live"), new Secret("test")),
            Network: NetworkSelection.SystemDefault,
            Reconnect: new ReconnectPolicy(3, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)));

        await engine.StartAsync(session);
        await WaitForAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(20));

        // A layer is moved while live; a reconnect must keep it where it was moved to.
        var moved = new NormalizedRect(0.6, 0.6, 0.3, 0.3);
        await engine.MoveLayerAsync("overlay-test", moved);

        // Live long enough that the first FFmpeg's frame count is beyond what the next reaches in 10 s.
        await Task.Delay(TimeSpan.FromSeconds(13));
        transport.DropCurrent();

        await WaitForAsync(() => transport.Connections >= 2, TimeSpan.FromSeconds(30));
        await WaitForAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(30));

        // Before the fix the second attempt was cut 11 s in as "no new frames".
        await Task.Delay(TimeSpan.FromSeconds(15));
        Assert.Equal(StreamState.Live, engine.State);
        Assert.Equal(2, transport.Connections);
        Assert.DoesNotContain(log.Lines, l => l.Contains("Stream stalled", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("Reconnect keeps the live layout", StringComparison.Ordinal) && l.Contains("overlay-test", StringComparison.Ordinal));

        await engine.StopAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, "Timed out waiting for the stream.");
            await Task.Delay(100);
        }
    }
}
