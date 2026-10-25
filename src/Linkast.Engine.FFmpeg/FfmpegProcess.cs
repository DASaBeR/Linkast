// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Supervises one ffmpeg.exe process: launch without a shell, line-based stdout/stderr callbacks,
/// graceful stop via 'q', forced kill after a grace period, and job-object lifetime binding.
/// </summary>
public sealed class FfmpegProcess : IAsyncDisposable
{
    private static readonly Lazy<JobObject?> SharedJob = new(JobObject.TryCreate);

    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;

    private FfmpegProcess(Process process, Action<string> onStdout, Action<string> onStderr)
    {
        _process = process;
        _stdoutPump = PumpAsync(process.StandardOutput, onStdout);
        _stderrPump = PumpAsync(process.StandardError, onStderr);
        _ = WaitForExitAsync();
    }

    public int ProcessId => _process.Id;

    /// <summary>Completes with the exit code once the process and its output pumps have finished.</summary>
    public Task<int> Exited => _exited.Task;

    public static FfmpegProcess Start(string ffmpegPath, IReadOnlyList<string> arguments, Action<string> onStdout, Action<string> onStderr)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(onStdout);
        ArgumentNullException.ThrowIfNull(onStderr);

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new StreamFailureException(
                new StreamError(ErrorCategory.Process, "process.start", "FFmpeg could not be started.", ex.Message), ex);
        }

        try
        {
            SharedJob.Value?.Assign(process);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Already in a non-nestable job (rare on Windows 8+). Lifetime binding is best effort.
        }

        try
        {
            // Encoding must not be starved by the game it is capturing.
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process may already have exited; the exit path reports why.
        }

        return new FfmpegProcess(process, onStdout, onStderr);
    }

    /// <summary>Asks FFmpeg to finish (writes the FLV trailer), then kills it after <paramref name="grace"/>.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (_exited.Task.IsCompleted)
        {
            return;
        }

        try
        {
            await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // stdin already closed: the process is exiting.
        }

        if (await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false) != _exited.Task)
        {
            Kill();
        }

        await _exited.Task.ConfigureAwait(false);
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Raced with a normal exit.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Job object will clean up when Linkast exits.
        }

        _process.Dispose();
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Pumps hung on a grandchild holding the pipe; exit code is still valid.
        }

        _exited.TrySetResult(_process.ExitCode);
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> sink)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                sink(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Stream closed by process exit.
        }
    }
}
