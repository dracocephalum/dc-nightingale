using System.Data.Common;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>What a value too long for its column is called, read off the message the database raised.</summary>
public sealed class DbExceptionExtensionsTests
{
    [Theory]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_streams', column 'id'. Truncated value: 'orders'.", ValueTooLongException.StreamName)]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_events', column 'stream_id'. Truncated value: 'orders'.", ValueTooLongException.StreamName)]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_events', column 'type'. Truncated value: 'order'.", "event type")]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_events', column 'correlation_id'. Truncated value: 'c'.", "correlation id")]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_events', column 'causation_id'. Truncated value: 'c'.", "causation id")]
    [InlineData("String or binary data would be truncated in table 'store.dbo.pc_events', column 'headers'. Truncated value: '{'.", "value")]
    [InlineData("String or binary data would be truncated.", "value")]
    public void DescribeColumn_ShouldNameWhatTheColumnHoldsAndCallAnythingElseAValue(string message, string expected)
    {
        // Arrange
        var sut = new DatabaseError(message);

        // Act
        var described = sut.DescribeColumn();

        // Assert
        described.ShouldBe(expected);
    }

    /// <summary>An error with the message a database raised; the provider's own cannot be made outside it.</summary>
    private sealed class DatabaseError(string message) : DbException(message);
}
