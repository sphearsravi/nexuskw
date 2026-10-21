namespace Nexus.Infrastructure.Email;

/// <summary>
/// When to try again after a transient failure.
/// <para>
/// Six retries over about eighteen hours. The jitter is not decoration: without it a
/// provider coming back from an outage receives the entire backlog in one burst and
/// throttles us straight back into the same state.
/// </para>
/// </summary>
public sealed class EmailRetryPolicy(TimeProvider clock)
{
    public static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(12)
    ];

    public const double JitterFraction = 0.2;

    public static int MaxAttempts => Schedule.Length + 1;

    /// <summary>
    /// When the row should next be tried, or null when the schedule is spent and the row
    /// is abandoned.
    /// </summary>
    /// <param name="attemptCount">Attempts made so far, including the one that just failed.</param>
    public DateTimeOffset? NextAttemptAt(int attemptCount, Random jitter)
    {
        var index = attemptCount - 1;
        if (index < 0) index = 0;
        if (index >= Schedule.Length) return null;

        var delay = Schedule[index];
        var spread = 1.0 + ((jitter.NextDouble() * 2.0) - 1.0) * JitterFraction;

        return clock.GetUtcNow() + TimeSpan.FromTicks((long)(delay.Ticks * spread));
    }

    /// <summary>The window a given attempt's next try must land in, used by the test and by the admin view.</summary>
    public static (TimeSpan Min, TimeSpan Max) Window(int attemptCount)
    {
        var delay = Schedule[Math.Clamp(attemptCount - 1, 0, Schedule.Length - 1)];
        return (delay * (1 - JitterFraction), delay * (1 + JitterFraction));
    }
}
