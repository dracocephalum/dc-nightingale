using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The sequencer's loop around its lease, with no database: what it does when asking for the
/// lease fails. Numbering itself needs the events table and is the integration tests'.
/// </summary>
public sealed class OrdinalSequencerTests
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
        A.CallTo(() => leases.AcquireLeaseAsync(OrdinalSequencer.LeaseName, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._))
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
        using var sut = new OrdinalSequencer(leases, A.Fake<IStoreTail>(), new SubscriptionGroupRegistry(), "Server=example;Database=nightingale", "dbo", "nightingale", time, NullLogger<OrdinalSequencer>.Instance);

        // Act: the loop waits out the interval after the failure, then asks again.
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await askedOnce.Task.WaitAsync(TestContext.Current.CancellationToken);
        var beforeTheInterval = askedTwice.Task.IsCompleted;
        while (!askedTwice.Task.IsCompleted)
        {
            time.Advance(OrdinalSequencer.RenewInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert: the loop is still running after the failure; it did not end with it.
        beforeTheInterval.ShouldBeFalse();
        asked.ShouldBeGreaterThanOrEqualTo(2);
    }
}
