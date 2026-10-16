using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The sequencer's host around its lease, with no database: what it does when asking for the
/// lease fails, and when a sequence's batches fail again and again. Numbering itself needs the
/// events table and is the integration tests'.
/// </summary>
public sealed class OrdinalSequencerHostTests
{
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Run_WhenAskingForTheLeaseFails_ShouldTryAgainAfterTheIntervalWhateverTheFailure(Type thrown)
    {
        // Arrange: the first ask fails, with a cancellation nobody asked for or with anything
        // else; from the second on another instance holds the lease.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero));
        var leases = A.Fake<ISubscriptionGroupStore>();
        var asked = 0;
        var askedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var askedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => leases.AcquireLeaseAsync(OrdinalSequencing.LeaseName, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                if (Interlocked.Increment(ref asked) == 1)
                {
                    askedOnce.TrySetResult();
                    throw (Exception)Activator.CreateInstance(thrown)!;
                }

                askedTwice.TrySetResult();
                return Task.FromResult<LeaseHolder?>(new LeaseHolder("another-instance", null));
            });
        var tail = new FollowedTail();
        var sequences = new SharedSequence(tail, tail.Numbering);
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), A.Fake<IOrdinalSequencer>(), new FakeOrdinalSequencingFaults(), time, NullLogger<OrdinalSequencerHost>.Instance);

        // Act: the loop waits out the interval after the failure, then asks again.
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await askedOnce.Task.WaitAsync(TestContext.Current.CancellationToken);
        var beforeTheInterval = askedTwice.Task.IsCompleted;
        while (!askedTwice.Task.IsCompleted)
        {
            time.Advance(sut.RenewInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: the loop is still running after the failure; it did not end with it.
        beforeTheInterval.ShouldBeFalse();
        asked.ShouldBeGreaterThanOrEqualTo(2);
        sut.RenewInterval.ShouldBe(OrdinalSequencing.LeaseDuration / 3);
    }

    [Fact]
    public async Task Run_WhenASequenceFailsAgainAndAgain_ShouldFaultItEverywhereAndClearTheFaultWhenABatchSucceeds()
    {
        // Arrange: this instance holds the lease; the head is at 10; the first three batches fail.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var leases = A.Fake<ISubscriptionGroupStore>();
        A.CallTo(() => leases.AcquireLeaseAsync(A<string>._, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).Returns(Task.FromResult<LeaseHolder?>(null));
        var tail = new FollowedTail();
        tail.Followed.Publish(10);
        var sequences = new SharedSequence(tail, tail.Numbering);
        var sequencer = A.Fake<IOrdinalSequencer>();
        var batches = 0;
        A.CallTo(() => sequencer.SequenceBatchAsync(null, A<long>._, A<CancellationToken>._))
            .ReturnsLazily((string? _, long head, CancellationToken _) => Interlocked.Increment(ref batches) <= OrdinalSequencerHost.FaultAfter
                ? Task.FromException<long>(new InvalidOperationException("the batch timed out"))
                : Task.FromResult(head));
        var faults = new FakeOrdinalSequencingFaults();
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), sequencer, faults, time, NullLogger<OrdinalSequencerHost>.Instance);

        // Act: a pass a renewal interval, until the third failure; then until the next batch.
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await PassUntilAsync(time, sut, () => tail.NumberingStopped is not null);
        var stopped = tail.NumberingStopped;
        var faultedWait = await Should.ThrowAsync<OrdinalSequencerFaultedException>(() => tail.WaitForNumberedAsync(10, TestContext.Current.CancellationToken).AsTask());
        await PassUntilAsync(time, sut, () => tail.Numbered == 10);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: faulted only at the third failure, recorded for every instance, and lifted by the
        // batch that succeeded, which records that too.
        stopped.ShouldBeOfType<OrdinalSequencerFaultedException>().Cause.ShouldBe("the batch timed out");
        faultedWait.Cause.ShouldBe("the batch timed out");
        tail.NumberingStopped.ShouldBeNull();
        faults.Writes.ShouldBe([(null, "the batch timed out"), (null, null)]);
    }

    [Fact]
    public async Task Run_WhenItTakesTheLeaseOverAFaultAnotherHolderRecorded_ShouldClearItAtTheFirstBatchThatSucceeds()
    {
        // Arrange: the progress row says the shared sequence is faulted; this instance numbers it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var leases = A.Fake<ISubscriptionGroupStore>();
        A.CallTo(() => leases.AcquireLeaseAsync(A<string>._, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).Returns(Task.FromResult<LeaseHolder?>(null));
        var tail = new FollowedTail();
        tail.Followed.Publish(5);
        var sequences = new SharedSequence(tail, tail.Numbering);
        var sequencer = A.Fake<IOrdinalSequencer>();
        A.CallTo(() => sequencer.SequenceBatchAsync(null, A<long>._, A<CancellationToken>._)).ReturnsLazily((string? _, long head, CancellationToken _) => Task.FromResult(head));
        var faults = new FakeOrdinalSequencingFaults().Recording(null, "the batch timed out");
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), sequencer, faults, time, NullLogger<OrdinalSequencerHost>.Instance);

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await PassUntilAsync(time, sut, () => faults.Writes.Count > 0);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        tail.Numbered.ShouldBe(5);
        faults.Writes.ShouldBe([(null, null)]);
    }

    /// <summary>Lets the loop run a pass a renewal interval until a condition holds, or fails after a hundred.</summary>
    private static async Task PassUntilAsync(FakeTimeProvider time, OrdinalSequencerHost sut, Func<bool> condition)
    {
        for (var pass = 0; pass < 100 && !condition(); pass++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            time.Advance(sut.RenewInterval);
        }

        condition().ShouldBeTrue("the loop did not get there in a hundred passes");
    }
}
