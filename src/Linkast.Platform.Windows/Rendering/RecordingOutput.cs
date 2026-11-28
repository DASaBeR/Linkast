// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Microsoft.Extensions.Logging;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Owns one secondary recording FFmpeg process together with its video/audio named pipes and GPU output target.
/// Failures here are isolated from the primary stream attempt.
/// </summary>
public sealed class RecordingOutput : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _recentLines;
    private int _failed;
    private string? _detachReason;

    private RecordingOutput(
        string name,
        string path,
        EncoderSettings encoder,
        NamedPipeServerStream videoPipe,
        NamedPipeServerStream audioPipe,
        D3D11OutputTarget target,
        FfmpegProcess process,
        ConcurrentQueue<string> recentLines)
    {
        Name = name;
        Path = path;
        Encoder = encoder;
        VideoPipe = videoPipe;
        AudioPipe = audioPipe;
        Target = target;
        Process = process;
        _recentLines = recentLines;
    }

    public string Name { get; }

    public string Path { get; }

    public EncoderSettings Encoder { get; }

    public NamedPipeServerStream VideoPipe { get; }

    public NamedPipeServerStream AudioPipe { get; }

    public D3D11OutputTarget Target { get; }

    public FfmpegProcess Process { get; }

    public Task<int> Exited => Process.Exited;

    public bool HasFailed => Volatile.Read(ref _failed) == 1;

    public string? DetachReason => Volatile.Read(ref _detachReason);

    public IReadOnlyList<string> RecentLines => _recentLines.ToArray();

    /// <summary>Marks this recording as failed. Returns true only on the first call so events fire once.</summary>
    public bool TryMarkFailed() => Interlocked.Exchange(ref _failed, 1) == 0;

    public void NoteDetachReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Interlocked.CompareExchange(ref _detachReason, reason, null);
        }
    }

    public static RecordingOutput Start(
        D3D11DeviceManager devices,
        string ffmpegPath,
        Redactor redactor,
        string name,
        string path,
        EncoderSettings encoder,
        Action<string>? onDiagnosticLine = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encoder);

        var videoPipeName = $"linkast-video-{Guid.NewGuid():N}";
        var audioPipeName = $"linkast-audio-{Guid.NewGuid():N}";
        var videoPipePath = @"\\.\pipe\" + videoPipeName;
        var audioPipePath = @"\\.\pipe\" + audioPipeName;
        var nv12Size = (int)(encoder.Width * encoder.Height * 1.5);

        NamedPipeServerStream? videoPipe = null;
        NamedPipeServerStream? audioPipe = null;
        D3D11OutputTarget? target = null;
        try
        {
            videoPipe = new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                nv12Size * 4);

            audioPipe = new NamedPipeServerStream(
                audioPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                192000);

            target = new D3D11OutputTarget(devices, name, encoder.Width, encoder.Height, videoPipe, isPrimary: false, logger);

            var job = new SinglePipeFfmpegJob(
                VideoPipePath: videoPipePath,
                Width: encoder.Width,
                Height: encoder.Height,
                Fps: encoder.Fps,
                AudioPipePath: audioPipePath,
                Encoder: encoder,
                PublishUrl: null,
                TcUrl: null,
                RecordingPath: path);

            var args = FfmpegArgumentBuilder.BuildSinglePipe(job);
            onDiagnosticLine?.Invoke(redactor.Redact($"ffmpeg [{name}]: " + string.Join(' ', args)));

            var recentLines = new ConcurrentQueue<string>();
            var process = FfmpegProcess.Start(
                ffmpegPath,
                args,
                _ => { },
                line =>
                {
                    var redacted = redactor.Redact(line);
                    recentLines.Enqueue(redacted);
                    while (recentLines.Count > 60 && recentLines.TryDequeue(out _)) { }
                    onDiagnosticLine?.Invoke($"[{name}] {redacted}");
                });

            return new RecordingOutput(name, path, encoder, videoPipe, audioPipe, target, process, recentLines);
        }
        catch
        {
            target?.Dispose();
            videoPipe?.Dispose();
            audioPipe?.Dispose();
            throw;
        }
    }

    public async Task<StreamError?> HandshakeAsync(MediaOutputScheduler scheduler, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeout);

        try
        {
            var videoConnect = VideoPipe.WaitForConnectionAsync(connectCts.Token);
            var first = await Task.WhenAny(videoConnect, Process.Exited).ConfigureAwait(false);
            if (first == Process.Exited)
            {
                await connectCts.CancelAsync().ConfigureAwait(false);
                return ClassifyEarlyExit(Process, _recentLines);
            }
            await videoConnect.ConfigureAwait(false);

            scheduler.RenderSingleFrame(Target);

            var audioConnect = AudioPipe.WaitForConnectionAsync(connectCts.Token);
            first = await Task.WhenAny(audioConnect, Process.Exited).ConfigureAwait(false);
            if (first == Process.Exited)
            {
                await connectCts.CancelAsync().ConfigureAwait(false);
                return ClassifyEarlyExit(Process, _recentLines);
            }
            await audioConnect.ConfigureAwait(false);

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            bool videoConnected = VideoPipe.IsConnected;
            bool audioConnected = AudioPipe.IsConnected;
            bool isAlive = !Process.HasExited;
            int? exitCode = Process.ExitCode;
            string lastErrorText = string.Join(Environment.NewLine, _recentLines);

            Process.Kill();

            var detail = $"Pipes: video={(videoConnected ? "connected" : "waiting")}, audio={(audioConnected ? "connected" : "waiting")}. " +
                $"Process: {(isAlive ? $"running (PID {Process.ProcessId})" : $"exited with code {exitCode}")}." +
                (string.IsNullOrWhiteSpace(lastErrorText) ? "" : $" Stderr: {lastErrorText}");

            return new StreamError(ErrorCategory.Process, "pipe.connect_timeout", "FFmpeg failed to connect to internal media pipes.", detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Process.Kill();
            throw;
        }
        catch (IOException ex)
        {
            await connectCts.CancelAsync().ConfigureAwait(false);
            if (Process.HasExited)
            {
                return ClassifyEarlyExit(Process, _recentLines);
            }

            var winner = await Task.WhenAny(Process.Exited, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            if (winner == Process.Exited)
            {
                return ClassifyEarlyExit(Process, _recentLines);
            }

            Process.Kill();
            return new StreamError(ErrorCategory.Process, "pipe.io_error", "Failed to communicate with FFmpeg media pipes.", ex.Message);
        }
    }

    public Task StopAsync(TimeSpan grace) => Process.StopAsync(grace);

    public async ValueTask DisposeAsync()
    {
        await Process.DisposeAsync().ConfigureAwait(false);
        Target.Dispose();
        await VideoPipe.DisposeAsync().ConfigureAwait(false);
        await AudioPipe.DisposeAsync().ConfigureAwait(false);
    }

    internal static StreamError ClassifyEarlyExit(FfmpegProcess process, ConcurrentQueue<string> recentLines)
    {
        var exitCode = process.ExitCode ?? -1;
        var lines = recentLines.ToArray();
        var classified = ErrorClassifier.ClassifyExit(exitCode, lines);
        if (classified.Code != "process.exit")
        {
            return classified;
        }

        var stderr = string.Join(Environment.NewLine, lines);
        return new StreamError(
            ErrorCategory.Process,
            "ffmpeg.early_exit",
            "FFmpeg exited unexpectedly during startup.",
            string.IsNullOrWhiteSpace(stderr) ? $"Exit code {exitCode}." : $"Exit code {exitCode}: {stderr}");
    }
}
