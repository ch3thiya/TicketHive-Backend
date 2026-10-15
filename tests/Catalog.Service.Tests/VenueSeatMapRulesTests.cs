using Catalog.Service.Models;
using Xunit;

namespace Catalog.Service.Tests;

public class VenueSeatMapRulesTests
{
    [Fact]
    public void Validate_AcceptsDistinctSectionsWithinVenueCapacity()
    {
        var spec = new VenueSeatMapSpec([
            new VenueSeatMapSection("Stalls", 2, 10),
            new VenueSeatMapSection("Balcony", 1, 5)
        ]);

        Assert.Empty(VenueSeatMapRules.Validate(spec, 30));
        Assert.Equal(25, VenueSeatMapRules.SeatCount(spec));
    }

    [Fact]
    public void Validate_RejectsDuplicateNamesAndCapacityOverflow()
    {
        var spec = new VenueSeatMapSpec([
            new VenueSeatMapSection("Stalls", 2, 10),
            new VenueSeatMapSection("stalls", 1, 10)
        ]);

        var errors = VenueSeatMapRules.Validate(spec, 25);

        Assert.Contains(errors, error => error.Code == "DuplicateSection");
        Assert.Contains(errors, error => error.Code == "CapacityExceeded");
    }
}
