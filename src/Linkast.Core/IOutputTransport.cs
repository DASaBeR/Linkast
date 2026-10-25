// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>
/// Owns the outgoing connection for one stream attempt. The engine hands FFmpeg
/// <see cref="PublishUrl"/>; the transport is responsible for reaching the real destination
/// over the selected network interface and for measuring what is actually sent.
/// </summary>
public interface IOutputLease : IAsyncDisposable
{
    /// <summary>
    /// Full URL, including the stream key, that FFmpeg should publish to (may be a local relay endpoint).
    /// A string, not a <see cref="Uri"/>, so the key is passed through byte-for-byte. Treat as a secret.
    /// </summary>
    string PublishUrl { get; }

    /// <summary>The tcUrl FFmpeg must announce so the remote server sees its own address.</summary>
    string TcUrl { get; }

    /// <summary>True when <see cref="Snapshot"/> reports real socket counters.</summary>
    bool IsMeasured { get; }

    NetworkSnapshot Snapshot();

    /// <summary>
    /// Completes when the outbound connection ends. Faults with <see cref="StreamFailureException"/>
    /// carrying a classified <see cref="StreamError"/> when it ends abnormally.
    /// </summary>
    Task Completion { get; }
}

public interface IOutputTransport
{
    /// <summary>Prepares an output for one attempt. Does not need the remote host to be reachable yet.</summary>
    Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken);
}

/// <summary>Carries a classified error across async boundaries.</summary>
public sealed class StreamFailureException : Exception
{
    public StreamFailureException(StreamError error)
        : base(error?.UserMessage)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public StreamFailureException(StreamError error, Exception inner)
        : base(error?.UserMessage, inner)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public StreamFailureException()
        : this(new StreamError(ErrorCategory.Unknown, "unknown", "Unknown streaming failure."))
    {
    }

    public StreamFailureException(string message)
        : this(new StreamError(ErrorCategory.Unknown, "unknown", message))
    {
    }

    public StreamFailureException(string message, Exception inner)
        : this(new StreamError(ErrorCategory.Unknown, "unknown", message), inner)
    {
    }

    public StreamError Error { get; }
}
