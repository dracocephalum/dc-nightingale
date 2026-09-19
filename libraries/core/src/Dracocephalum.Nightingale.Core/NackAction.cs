namespace Dracocephalum.Nightingale;

/// <summary>What a consumer asks the server to do with an event it could not process.</summary>
public enum NackAction
{
    /// <summary>Redeliver, counting a retry; parked when the group's limit is reached.</summary>
    Retry = 0,

    /// <summary>Park now, whatever the retry count.</summary>
    Park = 1,

    /// <summary>Treat as done without processing: not redelivered, not parked.</summary>
    Skip = 2,
}
