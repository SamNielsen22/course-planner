namespace Ingest.Gpa;

/// <summary>
/// A statistic no grade point average could have: a deviation above the
/// ceiling for that class size, or percentiles running backwards. The sign of
/// a value read out of the wrong field, so the row is skipped rather than written.
/// </summary>
public sealed class ImpossibleValueException(string message) : Exception(message);

/// <summary>
/// The interning dictionary does not match this response. Values are indexed
/// by position into segments the client accumulates, and the server reuses
/// segment numbers, so the two can drift apart. Writing such a row is worse
/// than skipping it.
/// </summary>
public sealed class StaleDictionaryException(string message) : Exception(message);

/// <summary>
/// The response did not carry the sheet or dropdown we were about to read,
/// since a worksheet is sent only when its rendering changed. Always retried,
/// never filled in from an earlier read.
/// </summary>
public sealed class NotRefreshedException(string message) : Exception(message);

/// <summary>The session has stopped returning any worksheet and will not recover.</summary>
public sealed class SessionWedgedException(string message) : Exception(message);

/// <summary>The server refused a request outright: a non-200 status, or a bootstrap that never loaded.</summary>
public sealed class RefusedException(string message) : Exception(message);

public static class Transport
{
    /// <summary>Timeouts and dropped connections leave the session alive, so they are retried on it.</summary>
    public static bool IsTransportError(Exception error) =>
        error is HttpRequestException or TaskCanceledException or IOException
        || (error is AggregateException a && a.InnerExceptions.All(IsTransportError));
}
