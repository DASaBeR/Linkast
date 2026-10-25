// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Linkast.Core.Camera;
using NetMQ;
using NetMQ.Sockets;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Sends runtime commands to a running FFmpeg through its <c>zmq</c> filter (loopback only), so
/// camera layers can be moved and resized without restarting the stream.
/// </summary>
public sealed class FfmpegCommandChannel : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _endpoint;
    private RequestSocket? _socket;
    private bool _disposed;

    public FfmpegCommandChannel(int port)
    {
        Port = port;
        _endpoint = $"tcp://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public int Port { get; }

    /// <summary>Finds a free loopback port for FFmpeg's zmq filter to bind.</summary>
    public static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Sends one command, for example <c>overlay@cam0 x 120</c>. Returns true when FFmpeg reports success.</summary>
    public async Task<bool> SendAsync(string target, string command, string argument, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(argument);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => SendBlocking($"{target} {command} {argument}"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves and resizes camera layer <paramref name="index"/> to <paramref name="placement"/> (output pixels).</summary>
    public async Task<bool> MoveLayerAsync(int index, PixelRect placement, CancellationToken cancellationToken = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var scale = $"scale@cam{index.ToString(inv)}";
        var overlay = $"overlay@cam{index.ToString(inv)}";
        var ok = await SendAsync(scale, "w", placement.Width.ToString(inv), cancellationToken).ConfigureAwait(false);
        ok &= await SendAsync(scale, "h", placement.Height.ToString(inv), cancellationToken).ConfigureAwait(false);
        ok &= await SendAsync(overlay, "x", placement.X.ToString(inv), cancellationToken).ConfigureAwait(false);
        ok &= await SendAsync(overlay, "y", placement.Y.ToString(inv), cancellationToken).ConfigureAwait(false);
        return ok;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket?.Dispose();
        _gate.Dispose();
    }

    private bool SendBlocking(string message)
    {
        _socket ??= Connect();
        if (!_socket.TrySendFrame(ReplyTimeout, message) || !_socket.TryReceiveFrameString(ReplyTimeout, out var reply))
        {
            // A REQ socket is stuck after a missed reply; start over on the next command.
            _socket.Dispose();
            _socket = null;
            return false;
        }

        // FFmpeg replies "<error code> <message>", with 0 meaning success.
        return reply.StartsWith("0 ", StringComparison.Ordinal) || reply == "0";
    }

    private RequestSocket Connect()
    {
        var socket = new RequestSocket();
        socket.Options.Linger = TimeSpan.Zero;
        socket.Connect(_endpoint);
        return socket;
    }
}
