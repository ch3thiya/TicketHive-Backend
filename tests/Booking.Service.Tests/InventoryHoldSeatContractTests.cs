using System.Text.Json;
using Booking.Service.Clients;
using Xunit;

namespace Booking.Service.Tests;

public class InventoryHoldSeatContractTests
{
    [Fact]
    public void ReadsTheSeatsInventoryReturnsOnAHold()
    {
        // The exact shape Inventory's InternalHoldResponse produces (web defaults, camelCase).
        const string json = "{\"holdId\":\"11111111-1111-1111-1111-111111111111\",\"showId\":\"22222222-2222-2222-2222-222222222222\",\"customerSub\":\"c\",\"status\":\"Active\",\"expiresAt\":\"2026-10-11T12:00:00+00:00\",\"items\":[{\"categoryId\":\"33333333-3333-3333-3333-333333333333\",\"quantity\":1,\"unitPrice\":100.0,\"currency\":\"LKR\"}],\"seats\":[{\"seatId\":\"44444444-4444-4444-4444-444444444444\",\"categoryId\":\"33333333-3333-3333-3333-333333333333\",\"section\":\"Front\",\"row\":\"A\",\"number\":7}]}";

        var hold = JsonSerializer.Deserialize<InventoryHoldResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var seat = Assert.Single(hold.Seats!);
        Assert.Equal(("Front", "A", 7), (seat.Section, seat.Row, seat.Number));
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), seat.SeatId);
    }

    [Fact]
    public void ReadsAGeneralAdmissionHoldWithoutSeats()
    {
        const string json = "{\"holdId\":\"11111111-1111-1111-1111-111111111111\",\"showId\":\"22222222-2222-2222-2222-222222222222\",\"customerSub\":\"c\",\"status\":\"Active\",\"expiresAt\":\"2026-10-11T12:00:00+00:00\",\"items\":[]}";

        var hold = JsonSerializer.Deserialize<InventoryHoldResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(hold.Seats is null || hold.Seats.Count == 0);
    }
}
