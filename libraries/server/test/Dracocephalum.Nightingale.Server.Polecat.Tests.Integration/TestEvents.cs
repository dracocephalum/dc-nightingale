using System.Text;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>The event most tests here append: one whose content the test does not care about.</summary>
internal static class TestEvents
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");

    /// <summary>An event with an id of its own and a small JSON body, of the type given.</summary>
    public static EventData Event(string type = "order_placed") => new(Guid.NewGuid(), type, Body);
}
