// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Lifecycle states of a streaming session.</summary>
public enum StreamState
{
    Idle,
    Preparing,
    Connecting,
    Live,
    Reconnecting,
    Stopping,
    Stopped,
    Failed,
}

public sealed class StreamStateChangedEventArgs(StreamState previous, StreamState current, StreamError? error) : EventArgs
{
    public StreamState Previous { get; } = previous;
    public StreamState Current { get; } = current;
    public StreamError? Error { get; } = error;
}

/// <summary>
/// Guards the allowed transitions so the engine cannot drift into an impossible state
/// (for example Live without passing through Connecting).
/// </summary>
public sealed class StreamStateMachine
{
    private static readonly Dictionary<StreamState, StreamState[]> Allowed = new()
    {
        [StreamState.Idle] = [StreamState.Preparing],
        [StreamState.Preparing] = [StreamState.Connecting, StreamState.Stopping, StreamState.Failed],
        [StreamState.Connecting] = [StreamState.Live, StreamState.Reconnecting, StreamState.Stopping, StreamState.Failed],
        [StreamState.Live] = [StreamState.Reconnecting, StreamState.Stopping, StreamState.Failed],
        [StreamState.Reconnecting] = [StreamState.Connecting, StreamState.Stopping, StreamState.Failed],
        [StreamState.Stopping] = [StreamState.Stopped, StreamState.Failed],
        [StreamState.Stopped] = [StreamState.Preparing],
        [StreamState.Failed] = [StreamState.Preparing],
    };

    private readonly Lock _gate = new();

    public StreamState Current { get; private set; } = StreamState.Idle;

    public event EventHandler<StreamStateChangedEventArgs>? Changed;

    public static bool CanTransition(StreamState from, StreamState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public bool TryTransition(StreamState to, StreamError? error = null)
    {
        StreamState previous;
        lock (_gate)
        {
            if (!CanTransition(Current, to))
            {
                return false;
            }

            previous = Current;
            Current = to;
        }

        Changed?.Invoke(this, new StreamStateChangedEventArgs(previous, to, error));
        return true;
    }

    public void Transition(StreamState to, StreamError? error = null)
    {
        if (!TryTransition(to, error))
        {
            throw new InvalidOperationException($"Invalid stream state transition {Current} -> {to}.");
        }
    }

    public static bool IsActive(StreamState state) =>
        state is StreamState.Preparing or StreamState.Connecting or StreamState.Live or StreamState.Reconnecting or StreamState.Stopping;
}
