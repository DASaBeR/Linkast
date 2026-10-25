// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Networking;

/// <summary>
/// Lets FFmpeg connect to the ingest server itself. No interface control and no socket counters;
/// kept as a diagnostic fallback to compare against the relay.
/// </summary>
public sealed class DirectOutputTransport : IOutputTransport
{
    public Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(network);
        if (!network.IsSystemDefault)
        {
            throw new StreamFailureException(new StreamError(
                ErrorCategory.Configuration, "network.selection.direct", "The direct transport cannot select a network interface."));
        }

        var url = destination.ServerUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + destination.StreamKey.Reveal();
        return Task.FromResult<IOutputLease>(new DirectLease(url));
    }

    private sealed class DirectLease(string url) : IOutputLease
    {
        private readonly TaskCompletionSource _never = new();

        public string PublishUrl { get; } = url;

        public string TcUrl => PublishUrl[..PublishUrl.LastIndexOf('/')];

        public bool IsMeasured => false;

        public Task Completion => _never.Task;

        public NetworkSnapshot Snapshot() => new(0, 0, 0, null, null, null, Connected: true);

        public ValueTask DisposeAsync()
        {
            _never.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
