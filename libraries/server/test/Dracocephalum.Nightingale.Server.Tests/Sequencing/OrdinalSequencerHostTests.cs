using Dracocephalum.Nightingale.Server.Sequencing;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The sequencer's loop around its lease, with no database: what it does when asking for the
/// lease fails. Numbering itself needs the events table and is the integration tests'.
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
        using var sut = new OrdinalSequencerHost(leases, sequences, follower, new SubscriptionGroupRegistry(), A.Fake<IOrdinalSequencer>(), time, NullLogger<OrdinalSequencerHost>.Instance);

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
}
