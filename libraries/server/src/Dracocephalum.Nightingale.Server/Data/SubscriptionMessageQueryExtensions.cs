namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>The filters the group store puts on a query of a group's parked messages or of its outbox.</summary>
internal static class SubscriptionMessageQueryExtensions
{
    /// <summary>The parked messages of one group.</summary>
    public static IQueryable<SubscriptionParkedEvent> FilterByGroup(this IQueryable<SubscriptionParkedEvent> parked, Guid groupId) =>
        parked.Where(row => row.SubscriptionGroupId == groupId);

    /// <summary>The outbox of one group.</summary>
    public static IQueryable<SubscriptionOutboxEntry> FilterByGroup(this IQueryable<SubscriptionOutboxEntry> queued, Guid groupId) =>
        queued.Where(row => row.SubscriptionGroupId == groupId);

    /// <summary>The parked messages at a number, below a number, or all of them when neither is given, in the numbering named.</summary>
    public static IQueryable<SubscriptionParkedEvent> FilterByNumber(this IQueryable<SubscriptionParkedEvent> parked, long? number, long? before, SubscriptionParkedNumber by)
    {
        if (number is { } wanted)
        {
            parked = by switch
            {
                SubscriptionParkedNumber.Revision => parked.Where(row => row.Revision == wanted),
                SubscriptionParkedNumber.Ordinal => parked.Where(row => row.Ordinal == wanted),
                _ => parked.Where(row => row.Position == wanted),
            };
        }

        if (before is { } below)
        {
            parked = by switch
            {
                SubscriptionParkedNumber.Revision => parked.Where(row => row.Revision < below),
                SubscriptionParkedNumber.Ordinal => parked.Where(row => row.Ordinal < below),
                _ => parked.Where(row => row.Position < below),
            };
        }

        return parked;
    }

    /// <summary>The outbox entries at a number, below a number, or all of them when neither is given, in the numbering named.</summary>
    public static IQueryable<SubscriptionOutboxEntry> FilterByNumber(this IQueryable<SubscriptionOutboxEntry> queued, long? number, long? before, SubscriptionParkedNumber by)
    {
        if (number is { } wanted)
        {
            queued = by switch
            {
                SubscriptionParkedNumber.Revision => queued.Where(row => row.Revision == wanted),
                SubscriptionParkedNumber.Ordinal => queued.Where(row => row.Ordinal == wanted),
                _ => queued.Where(row => row.Position == wanted),
            };
        }

        if (before is { } below)
        {
            queued = by switch
            {
                SubscriptionParkedNumber.Revision => queued.Where(row => row.Revision < below),
                SubscriptionParkedNumber.Ordinal => queued.Where(row => row.Ordinal < below),
                _ => queued.Where(row => row.Position < below),
            };
        }

        return queued;
    }
}
