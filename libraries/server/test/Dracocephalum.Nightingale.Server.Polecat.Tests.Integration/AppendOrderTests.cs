using System.Text;

using Dracocephalum.Nightingale.Server.Contracts;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// What the horizons' minimum age of zero rests on: the store writes an append's stream row before
/// it takes a position, so the append's transaction is a writer the horizon sees, holding the
/// row's lock in its partition, before it holds a position nobody can see. Pinned against the
/// store as it ships, so a
/// version that reorders its statements fails here rather than leaving a head that can pass an
/// append still to commit (<c>docs/rules/store-upgrades.md</c>). The append is stopped between its
/// two writes by a session that holds the events table under an exclusive lock and writes nothing,
/// so that it is no writer itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AppendOrderTests
{
    [Fact]
    public async Task Append_ShouldBeAWriterTheHorizonSeesBeforeItTakesAPosition()
    {
        // Arrange
        var name = TestDatabases.NewName();
        try
        {
            using var host = await TestDatabases.StartProvisionedHostAsync(name);
            var horizon = SqlServerTransactionHorizon.Over(TestDatabases.ConnectionStringFor(name));
            await using var blocker = new SqlConnection(TestDatabases.ConnectionStringFor(name));
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using var hold = (SqlTransaction)await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var command = new SqlCommand("SELECT TOP (0) seq_id FROM dbo.pc_events WITH (TABLOCKX, HOLDLOCK);", blocker, hold))
            {
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                command.CommandText = "SELECT @@SPID;";
                var blockerSession = (short)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

                // Act: the append writes its stream row, then waits at the events table.
                var appending = host.Store().AppendAsync("ordered-1", StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"))], TestContext.Current.CancellationToken);
                var holder = await FindHolderAsync(horizon);
                await Task.Delay(500, TestContext.Current.CancellationToken);
                var later = await horizon.FindHolderAsync(await horizon.MarkAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

                // Assert: a writer other than the blocker is there while the append has no position,
                // and stays: the append's transaction, not a write that came and went.
                appending.IsCompleted.ShouldBeFalse("the append waits at the events table");
                holder.ShouldNotBeNull("the append is not a writer the horizon sees before it takes a position: the store writes the events before the stream row, and a minimum age of zero no longer holds");
                holder.SessionId.ShouldNotBe(blockerSession);
                later.ShouldNotBeNull().SessionId.ShouldBe(holder.SessionId);

                await hold.RollbackAsync(TestContext.Current.CancellationToken);
                (await appending).Position.ShouldBeGreaterThan(0);
            }

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await TestDatabases.DropAsync(name);
        }
    }

    /// <summary>Asks the horizon, with a marker taken now, until it names a holder or ten seconds pass.</summary>
    private static async Task<HorizonHolder?> FindHolderAsync(SqlServerTransactionHorizon horizon)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var holder = await horizon.FindHolderAsync(await horizon.MarkAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            if (holder is not null)
            {
                return holder;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
