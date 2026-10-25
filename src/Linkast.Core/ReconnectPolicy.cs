// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Bounded exponential backoff for network-class failures.</summary>
/// <param name="MaxAttempts">Attempts before giving up. Zero disables reconnect.</param>
/// <param name="StableResetAfter">After this long in Live, the attempt counter resets.</param>
public sealed record ReconnectPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaxDelay, TimeSpan StableResetAfter)
{
    public static ReconnectPolicy Default { get; } = new(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    public static ReconnectPolicy Disabled { get; } = new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>Stateful helper that turns a <see cref="ReconnectPolicy"/> into concrete delays.</summary>
public sealed class ReconnectBackoff(ReconnectPolicy policy)
{
    public int Attempts { get; private set; }

    public ReconnectPolicy Policy { get; } = policy;

    /// <summary>Returns the delay before the next attempt, or null when the budget is exhausted.</summary>
    public TimeSpan? NextDelay()
    {
        if (Attempts >= Policy.MaxAttempts)
        {
            return null;
        }

        var exponent = Math.Min(Attempts, 30);
        var ticks = Policy.InitialDelay.Ticks * Math.Pow(2, exponent);
        Attempts++;
        return TimeSpan.FromTicks((long)Math.Min(ticks, Policy.MaxDelay.Ticks));
    }

    /// <summary>Call while live; resets the budget once the connection has been stable long enough.</summary>
    public void ReportStable(TimeSpan liveDuration)
    {
        if (liveDuration >= Policy.StableResetAfter)
        {
            Attempts = 0;
        }
    }

    public void Reset() => Attempts = 0;
}
