using Catalog.Service.Models;
using Xunit;

namespace Catalog.Service.Tests;

public class VenueSeatMapRulesTests
{
    private static string[] Codes(VenueSeatMapSpec? spec, int venueCapacity) =>
        VenueSeatMapRules.Validate(spec, venueCapacity).Select(error => error.Code).ToArray();

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

    [Fact]
    public void Validate_RejectsMissingSections()
    {
        Assert.Equal(new[] { "NoSections" }, Codes(null, 10));
        Assert.Equal(new[] { "NoSections" }, Codes(new VenueSeatMapSpec([]), 10));
    }

    [Fact]
    public void Validate_AllowsMaximumSectionCountButRejectsOneMore()
    {
        var sections = Enumerable.Range(0, SeatLayoutRules.MaxSections)
            .Select(i => new VenueSeatMapSection($"S{i}", 1, 1))
            .ToArray();

        Assert.Empty(Codes(new VenueSeatMapSpec(sections), SeatLayoutRules.MaxSections));
        Assert.Equal(new[] { "TooLarge" }, Codes(
            new VenueSeatMapSpec([.. sections, new VenueSeatMapSection("OneTooMany", 1, 1)]),
            SeatLayoutRules.MaxSections + 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(SeatLayoutRules.MaxRowsPerSection + 1, 1)]
    [InlineData(1, SeatLayoutRules.MaxSeatsPerRow + 1)]
    public void Validate_RejectsEveryInvalidDimensionBoundary(int rows, int seatsPerRow)
    {
        var spec = new VenueSeatMapSpec([new VenueSeatMapSection("Stalls", rows, seatsPerRow)]);

        Assert.Contains("InvalidDimensions", Codes(spec, SeatLayoutRules.MaxSeats));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankSectionNames(string name)
    {
        var spec = new VenueSeatMapSpec([new VenueSeatMapSection(name, 1, 1)]);

        Assert.Contains("InvalidSectionName", Codes(spec, 1));
    }

    [Fact]
    public void Validate_AllowsMaximumNameAndCapacityBoundaries()
    {
        var maximumName = new string('a', SeatLayoutRules.MaxSectionNameLength);
        var spec = new VenueSeatMapSpec([
            new VenueSeatMapSection(maximumName, SeatLayoutRules.MaxRowsPerSection, 250)
        ]);

        Assert.Equal(SeatLayoutRules.MaxSeats, VenueSeatMapRules.SeatCount(spec));
        Assert.Empty(Codes(spec, SeatLayoutRules.MaxSeats));
    }

    [Fact]
    public void Validate_RejectsNameBeyondMaximumAndCapacityBeyondEitherLimit()
    {
        var longName = new string('a', SeatLayoutRules.MaxSectionNameLength + 1);
        var overVenue = new VenueSeatMapSpec([new VenueSeatMapSection("Stalls", 2, 10)]);
        var overGlobal = new VenueSeatMapSpec([
            new VenueSeatMapSection("A", SeatLayoutRules.MaxRowsPerSection, SeatLayoutRules.MaxSeatsPerRow),
            new VenueSeatMapSection("B", 1, 1)
        ]);

        Assert.Contains("InvalidSectionName", Codes(
            new VenueSeatMapSpec([new VenueSeatMapSection(longName, 1, 1)]), 1));
        Assert.Contains("CapacityExceeded", Codes(overVenue, 19));
        Assert.Contains("CapacityExceeded", Codes(overGlobal, int.MaxValue));
    }
}
