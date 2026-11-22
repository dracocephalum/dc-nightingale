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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_WhenACatchUpOutlastsTheRenewalInterval_ShouldRenewTheLeaseAndStopNumberingIfItIsLost(bool renewed)
    {
        // Arrange: the head is at 10 and the first batch reaches 4, taking a renewal interval;
        // the lease is then asked for again, and is this instance's still or another's.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var leases = A.Fake<ISubscriptionGroupStore>();
        var asked = 0;
        var askedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => leases.AcquireLeaseAsync(OrdinalSequencing.LeaseName, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                var holder = Interlocked.Increment(ref asked) == 1 || renewed ? null : new LeaseHolder("another-instance", null);
                if (asked == 2)
                {
                    askedAgain.TrySetResult();
                }

                return Task.FromResult(holder);
            });
        var tail = new FollowedTail();
        tail.Followed.Publish(10);
        var sequences = new SharedSequence(tail, tail.Numbering);
        var sequencer = A.Fake<IOrdinalSequencer>();
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), sequencer, new FakeOrdinalSequencingFaults(), time, NullLogger<OrdinalSequencerHost>.Instance);
        var batches = 0;
        A.CallTo(() => sequencer.SequenceBatchAsync(null, A<long>._, A<CancellationToken>._))
            .ReturnsLazily((string? _, long head, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref batches) > 1)
                {
                    return Task.FromResult(head);
                }

                time.Advance(sut.RenewInterval);
                return Task.FromResult(4L);
            });

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await askedAgain.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (renewed)
        {
            await tail.WaitForNumberedAsync(10, TestContext.Current.CancellationToken);
        }

        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: with the lease lost the numbering stays where the first batch left it, and the
        // lease, another's now, is not given back.
        tail.Numbered.ShouldBe(renewed ? 10 : 4);
        batches.ShouldBe(renewed ? 2 : 1);
        var released = A.CallTo(() => leases.ReleaseLeaseAsync(OrdinalSequencing.LeaseName, A<string>._, A<CancellationToken>._));
        if (renewed)
        {
            released.MustHaveHappenedOnceExactly();
        }
        else
        {
            released.MustNotHaveHappened();
        }
    }

    [Fact]
    public async Task Stop_WhenGivingTheLeaseBackFails_ShouldStillStop()
    {
        // Arrange: this instance holds the lease and has numbered up to the head.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var leases = A.Fake<ISubscriptionGroupStore>();
        A.CallTo(() => leases.AcquireLeaseAsync(A<string>._, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).Returns(Task.FromResult<LeaseHolder?>(null));
        A.CallTo(() => leases.ReleaseLeaseAsync(A<string>._, A<string>._, A<CancellationToken>._)).ThrowsAsync(new InvalidOperationException("The database is down."));
        var tail = new FollowedTail();
        tail.Followed.Publish(5);
        var sequences = new SharedSequence(tail, tail.Numbering);
        var sequencer = A.Fake<IOrdinalSequencer>();
        A.CallTo(() => sequencer.SequenceBatchAsync(null, A<long>._, A<CancellationToken>._)).ReturnsLazily((string? _, long head, CancellationToken _) => Task.FromResult(head));
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), sequencer, new FakeOrdinalSequencingFaults(), time, NullLogger<OrdinalSequencerHost>.Instance);
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await tail.WaitForNumberedAsync(5, TestContext.Current.CancellationToken);

        // Act
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: the lease was offered back, and its lapse is what frees it.
        A.CallTo(() => leases.ReleaseLeaseAsync(OrdinalSequencing.LeaseName, A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Run_WhenAFaultCannotBeRecorded_ShouldStillStopTheNumberingHereAndRecordItsEndOnceItCan()
    {
        // Arrange: the first three batches fail, and so do the write of the fault and the first
        // write of its end.
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
        var faults = A.Fake<IOrdinalSequencingFaults>();
        var written = new List<string?>();
        A.CallTo(() => faults.WriteAsync(null, A<string?>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsLazily((string? _, string? fault, DateTimeOffset _, CancellationToken _) =>
            {
                lock (written)
                {
                    written.Add(fault);
                    return written.Count <= 2 ? Task.FromException(new InvalidOperationException("The database is down.")) : Task.CompletedTask;
                }
            });
        var follower = new OrdinalSequencingFollower(sequences, new FakeOrdinalSequencingProgressReader(), time, NullLogger<OrdinalSequencingFollower>.Instance);
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), sequencer, faults, time, NullLogger<OrdinalSequencerHost>.Instance);

        // Act: a pass a renewal interval, until the fault; then until its end has been written.
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await PassUntilAsync(time, sut, () => tail.NumberingStopped is not null);
        var stopped = tail.NumberingStopped;
        await PassUntilAsync(time, sut, () => WrittenCount() >= 3);
        var lifted = tail.NumberingStopped;
        await PassUntilAsync(time, sut, () => batches >= OrdinalSequencerHost.FaultAfter + 4);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: the fault and its end are each tried; the end is tried again at the next batch
        // that succeeds, and no more once it is written.
        stopped.ShouldBeOfType<OrdinalSequencerFaultedException>().Cause.ShouldBe("the batch timed out");
        lifted.ShouldBeNull();
        tail.Numbered.ShouldBe(10);
        written.ShouldBe(["the batch timed out", null, null]);

        int WrittenCount()
        {
            lock (written)
            {
                return written.Count;
            }
        }
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
