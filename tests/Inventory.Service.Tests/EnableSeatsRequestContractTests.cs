using System.Text.Json;
using Inventory.Service.Controllers;
using Xunit;

namespace Inventory.Service.Tests;

public class EnableSeatsRequestContractTests
{
    [Fact]
    public void BindsTheJsonCatalogSends()
    {
        // The exact shape Catalog's InventorySeatClient produces (web defaults, camelCase).
        const string json = "{\"version\":3,\"seats\":[{\"categoryId\":\"11111111-1111-1111-1111-111111111111\",\"section\":\"Front\",\"row\":\"A\",\"number\":1}]}";

        var request = JsonSerializer.Deserialize<EnableSeatsRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(3, request.Version);
        var seat = Assert.Single(request.Seats);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), seat.CategoryId);
        Assert.Equal("Front", seat.Section);
        Assert.Equal("A", seat.Row);
        Assert.Equal(1, seat.Number);
    }
}
