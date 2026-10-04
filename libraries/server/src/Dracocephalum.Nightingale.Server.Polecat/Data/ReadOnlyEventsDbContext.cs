using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// The mirror of the store's events table over the read-only connection. It adds nothing to
/// <see cref="EventsDbContext"/> but its type, which is the point: the two are registered side
/// by side, each with its own connection, and a reader asks for the one it means, so the wrong
/// connection is a compile error rather than a string that did not match. It is always
/// registered; a host that turned the read-only connection off gets it over the main
/// connection string and nothing reads through it.
/// </summary>
/// <param name="options">The options, which name the provider and the read-only connection.</param>
/// <param name="schema">The schema the store's tables live in.</param>
internal sealed class ReadOnlyEventsDbContext(DbContextOptions<ReadOnlyEventsDbContext> options, EventStoreSchema schema)
    : EventsDbContext(options, schema);
