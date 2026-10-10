using Booking.Service.Models;
using Xunit;

namespace Booking.Service.Tests;

public class CancellationRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false, -1, "NotFound")]
    [InlineData(true, true, -1, "TicketUsed")]
    [InlineData(true, false, 0, "ShowStarted")]
    [InlineData(true, false, 1, "ShowStarted")]
    [InlineData(true, false, -1, null)]
    public void Customer_rule(bool owner, bool used, int seconds, string? expected) =>
        Assert.Equal(expected, CancellationRules.Refusal(OrderStatus.Confirmed, owner, used, Start, Start.AddSeconds(seconds), false));

    [Fact]
    public void Automatic_cancellation_allows_used_tickets_and_started_show() =>
        Assert.Null(CancellationRules.Refusal(OrderStatus.Confirmed, false, true, Start, Start.AddDays(1), true));

    [Fact]
    public void Repeat_does_not_bypass_ownership() =>
        Assert.Equal("NotFound", CancellationRules.Refusal(OrderStatus.Cancelled, false, false, Start, Start, false));
}
