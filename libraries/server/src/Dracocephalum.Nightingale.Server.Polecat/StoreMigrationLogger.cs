using System.Data.Common;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Where the store's schema tooling writes what it applies: the host's logging, under
/// <see cref="StoreSchema"/>'s category, at the levels Entity Framework logs the gateway's own
/// migrations at, so both halves of a start read the same way. The tooling hands over the
/// statements for one schema object at a time, a table with its indexes or the schema itself, so
/// a start that changed the event store's tables leaves one entry per object it touched, and a
/// failure one entry naming the statements that failed, after which the exception goes on to
/// whoever applied, as the tooling's own logger sends it.
/// </summary>
/// <param name="logger">The logger.</param>
/// <param name="database">The database's name, for the entries.</param>
internal sealed partial class StoreMigrationLogger(ILogger logger, string database) : IMigrationLogger
{
    /// <inheritdoc/>
    public void SchemaChange(string sql) => LogSchemaChange(database, sql);

    /// <inheritdoc/>
    public void OnFailure(DbCommand command, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(ex);
        LogFailure(ex, database, command.CommandText);
        ExceptionDispatchInfo.Throw(ex);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying to the event store's tables of database {Database}:\n{Sql}")]
    private partial void LogSchemaChange(string database, string sql);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed applying to the event store's tables of database {Database}; the statement that failed:\n{Sql}")]
    private partial void LogFailure(Exception exception, string database, string sql);
}
