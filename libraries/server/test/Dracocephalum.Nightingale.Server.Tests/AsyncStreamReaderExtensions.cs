using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>How a test reads a call that does not end on its own: a subscription, or a consumer's conversation.</summary>
internal static class AsyncStreamReaderExtensions
{
    /// <summary>
    /// How long the next message is waited for. A server that sends fewer messages than a test
    /// expects leaves the call open and silent, and nothing else ends that wait; long enough
    /// that a busy machine does not run into it.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>Moves to the next message, and fails saying what had come when none comes.</summary>
    /// <param name="responses">The call's responses.</param>
    /// <param name="seen">The messages the test has read so far, for the failure to show.</param>
    /// <returns>Whether there is a message; false when the call ended.</returns>
    public static async Task<bool> MoveNextOrFailAsync<T>(this IAsyncStreamReader<T> responses, IReadOnlyCollection<T> seen)
    {
        try
        {
            return await responses.MoveNext(TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The call sent nothing more in {Patience.TotalSeconds:0} seconds and did not end. It had sent {seen.Count}, the last of them: {string.Join(", ", seen.TakeLast(3))}");
        }
    }
}
