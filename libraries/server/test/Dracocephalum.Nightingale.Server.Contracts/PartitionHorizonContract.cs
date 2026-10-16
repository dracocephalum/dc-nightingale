using System.Data.Common;
using Dracocephalum.Nightingale.Server.Sequencing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Contracts;

/// <summary>
/// What every backend's horizon by store partition must do besides the
/// <see cref="TransactionHorizonContract"/>: a writer open in one partition holds that partition's
/// sequence, and no other's, and one call answers for every sequence asked. A derived class
/// supplies the horizon of a store partitioned in two, a sequence in each partition, and a writer
/// that has done in a partition what an append does first, and is left open.
/// </summary>
public abstract class PartitionHorizonContract
{
    /// <summary>How long a sequence expected to clear is given to clear: the horizon may read its partition's locks only after a delay.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(10);

    /// <summary>Gets the horizon of the store under test.</summary>
    protected abstract ITransactionHorizon Horizon { get; }

    /// <summary>Gets a sequence in one partition.</summary>
    protected abstract SequenceKey First { get; }

    /// <summary>Gets a sequence in another partition.</summary>
    protected abstract SequenceKey Second { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FindHolders_WithAWriterInOnePartition_ShouldHoldThatPartitionAlone()
    {
        await using var writer = await BeginWriterInAsync(First, Cancellation);
        var marker = await Horizon.MarkAsync(Cancellation);

        var holders = await SettleAsync(marker, holders => !holders.ContainsKey(Second));

        holders.ShouldContainKey(First);
        holders.ShouldNotContainKey(Second);
        holders[First].SessionId.ShouldNotBeNull();
    }

    [Fact]
    public async Task FindHolders_WithAWriterInEachPartition_ShouldHoldBoth()
    {
        await using var first = await BeginWriterInAsync(First, Cancellation);
        await using var second = await BeginWriterInAsync(Second, Cancellation);
        var marker = await Horizon.MarkAsync(Cancellation);

        // Past any delay before the locks are read, so the answer is the locks'.
        await Task.Delay(Settling / 4, Cancellation);
        var holders = await FindHoldersAsync(marker);

        holders.Keys.ShouldBe([First, Second], ignoreOrder: true);
    }

    [Fact]
    public async Task FindHolders_AfterTheWriterCommits_ShouldClearItsPartition()
    {
        await using var writer = await BeginWriterInAsync(First, Cancellation);
        var marker = await Horizon.MarkAsync(Cancellation);

        await writer.Transaction.CommitAsync(Cancellation);
        var holders = await SettleAsync(marker, holders => holders.Count == 0);

        holders.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindHolders_WithAWriterThatBeganAfterTheMarker_ShouldHoldNeither()
    {
        var marker = await Horizon.MarkAsync(Cancellation);

        // Well past the marker on any clock's rounding.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Cancellation);
        await using var writer = await BeginWriterInAsync(First, Cancellation);
        var holders = await SettleAsync(marker, holders => holders.Count == 0);

        holders.ShouldBeEmpty();
    }

    /// <summary>Begins a transaction that does in a sequence's partition what an append does first, and leaves it open.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open transaction, on a connection of its own.</returns>
    protected abstract Task<DbTransaction> BeginWriteAsync(SequenceKey sequence, CancellationToken cancellationToken);

    private async Task<OpenWriter> BeginWriterInAsync(SequenceKey sequence, CancellationToken cancellationToken) => new(await BeginWriteAsync(sequence, cancellationToken));

    private ValueTask<IReadOnlyDictionary<SequenceKey, HorizonHolder>> FindHoldersAsync(HorizonMarker marker) =>
        Horizon.FindHoldersAsync(new Dictionary<SequenceKey, HorizonMarker> { [First] = marker, [Second] = marker }, Cancellation);

    /// <summary>Asks both sequences until the answer settles as expected, or the time to settle is up, and returns the last answer.</summary>
    private async Task<IReadOnlyDictionary<SequenceKey, HorizonHolder>> SettleAsync(HorizonMarker marker, Func<IReadOnlyDictionary<SequenceKey, HorizonHolder>, bool> settled)
    {
        var until = TimeProvider.System.GetTimestamp() + (long)(Settling.TotalSeconds * TimeProvider.System.TimestampFrequency);
        IReadOnlyDictionary<SequenceKey, HorizonHolder> holders;
        while (!settled(holders = await FindHoldersAsync(marker)) && TimeProvider.System.GetTimestamp() < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        }

        return holders;
    }

    /// <summary>A writer's open transaction and its connection.</summary>
    private sealed class OpenWriter(DbTransaction transaction) : IAsyncDisposable
    {
        public DbTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            var connection = transaction.Connection;
            await transaction.DisposeAsync();
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
