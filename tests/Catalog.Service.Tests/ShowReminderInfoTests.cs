using Catalog.Service.Models;
using Xunit;

namespace Catalog.Service.Tests;

public class ShowReminderInfoTests
{
    private static ShowReminderInfo Info(string showStatus = "Active", string eventStatus = "Published") =>
        new(Guid.NewGuid(), new DateOnly(2026, 12, 1), new TimeOnly(19, 0), showStatus, "Gig", eventStatus, "Hall");

    [Fact]
    public void TheLocalShowTime_IsConvertedFromSriLankaTimeToUtc() =>
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 13, 30, 0, TimeSpan.Zero), Info().StartsAtUtc);

    [Fact]
    public void AMidnightShow_RollsBackToTheEarlierUtcDay()
    {
        var info = Info() with { ShowTime = new TimeOnly(1, 0) };

        Assert.Equal(new DateTimeOffset(2026, 11, 30, 19, 30, 0, TimeSpan.Zero), info.StartsAtUtc);
    }

    [Theory]
    [InlineData("Active", "Published", false)]
    [InlineData("Cancelled", "Published", true)]
    [InlineData("Active", "Cancelled", true)]
    [InlineData("Cancelled", "Cancelled", true)]
    public void CancelledMeansEitherTheShowOrItsEventWasCancelled(string show, string evt, bool expected) =>
        Assert.Equal(expected, Info(show, evt).Cancelled);
}
