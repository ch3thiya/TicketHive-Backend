using Catalog.Service.Models;
using Xunit;

namespace Catalog.Service.Tests;

public class SeatLayoutRulesTests
{
    private static readonly Guid Vip = Guid.NewGuid();
    private static readonly Guid General = Guid.NewGuid();

    private static List<TicketCategory> Categories(int vip = 20, int general = 100) => new()
    {
        new TicketCategory { Id = Vip, Name = "VIP", Capacity = vip },
        new TicketCategory { Id = General, Name = "General", Capacity = general }
    };

    private static SeatLayoutSpec Spec(params SeatLayoutSection[] sections) => new(sections);

    private static SeatLayoutSpec ValidSpec() => Spec(
        new SeatLayoutSection("Front", Vip, 2, 10),
        new SeatLayoutSection("Hall", General, 10, 10));

    private static string[] Codes(SeatLayoutSpec? spec, List<TicketCategory> categories) =>
        SeatLayoutRules.Validate(spec, categories).Select(e => e.Code).ToArray();

    [Fact]
    public void ValidLayout_HasNoErrors() => Assert.Empty(Codes(ValidSpec(), Categories()));

    [Fact]
    public void NoSections_IsRejected()
    {
        Assert.Equal(new[] { "NoSections" }, Codes(Spec(), Categories()));
        Assert.Equal(new[] { "NoSections" }, Codes(null, Categories()));
    }

    [Fact]
    public void ExactlyMaximumSections_IsAllowed_ButOneMoreIsRejected()
    {
        var sections = Enumerable.Range(0, SeatLayoutRules.MaxSections)
            .Select(i => new SeatLayoutSection($"S{i}", Vip, 1, 1))
            .ToArray();

        Assert.Empty(Codes(Spec(sections), [new TicketCategory
        {
            Id = Vip, Name = "VIP", Capacity = SeatLayoutRules.MaxSections
        }]));
        Assert.Equal(new[] { "TooLarge" }, Codes(
            Spec([.. sections, new SeatLayoutSection("OneTooMany", Vip, 1, 1)]),
            [new TicketCategory { Id = Vip, Name = "VIP", Capacity = SeatLayoutRules.MaxSections + 1 }]));
    }

    [Fact]
    public void UnassignedSection_IsRejected()
    {
        var spec = Spec(new SeatLayoutSection("Front", Guid.Empty, 2, 10), new SeatLayoutSection("Hall", General, 10, 10));

        Assert.Contains("UnassignedSection", Codes(spec, Categories()));
    }

    [Fact]
    public void CategoryFromAnotherShow_IsRejected()
    {
        var spec = Spec(new SeatLayoutSection("Front", Guid.NewGuid(), 2, 10), new SeatLayoutSection("Hall", General, 10, 10));

        Assert.Contains("UnknownCategory", Codes(spec, Categories()));
    }

    [Fact]
    public void InactiveCategory_CannotBeMapped()
    {
        var categories = Categories();
        categories[0].IsActive = false;

        Assert.Contains("UnknownCategory", Codes(ValidSpec(), categories));
    }

    [Theory]
    [InlineData("Front", "front")]
    [InlineData("Front", " Front ")]
    public void DuplicateSectionNames_AreRejected(string first, string second)
    {
        var spec = Spec(new SeatLayoutSection(first, Vip, 2, 10), new SeatLayoutSection(second, General, 10, 10));

        Assert.Contains("DuplicateSection", Codes(spec, Categories()));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(SeatLayoutRules.MaxRowsPerSection + 1, 10)]
    [InlineData(10, SeatLayoutRules.MaxSeatsPerRow + 1)]
    public void BadDimensions_AreRejectedWithTheDimensionCode(int rows, int seats)
    {
        var spec = Spec(new SeatLayoutSection("Front", Vip, rows, seats));

        Assert.Contains("InvalidDimensions", Codes(spec, Categories()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSectionName_IsRejectedWithTheNameCode(string name)
    {
        var spec = Spec(new SeatLayoutSection(name, Vip, 1, 1));

        Assert.Contains("InvalidSectionName", Codes(spec, [new TicketCategory { Id = Vip, Name = "VIP", Capacity = 1 }]));
    }

    [Fact]
    public void SectionName_AllowsMaximumLengthButRejectsOneMore()
    {
        var maximum = new string('a', SeatLayoutRules.MaxSectionNameLength);
        var category = new TicketCategory { Id = Vip, Name = "VIP", Capacity = 1 };

        Assert.Empty(Codes(Spec(new SeatLayoutSection(maximum, Vip, 1, 1)), [category]));
        Assert.Contains("InvalidSectionName", Codes(
            Spec(new SeatLayoutSection($"{maximum}a", Vip, 1, 1)), [category]));
    }

    [Fact]
    public void MaximumDimensionsAndTotal_AreAllowed()
    {
        var spec = Spec(new SeatLayoutSection(
            "Arena", Vip, SeatLayoutRules.MaxRowsPerSection, 250));

        Assert.Equal(SeatLayoutRules.MaxSeats, SeatLayoutRules.SeatCount(spec));
        Assert.Empty(Codes(spec, [new TicketCategory
        {
            Id = Vip, Name = "VIP", Capacity = SeatLayoutRules.MaxSeats
        }]));
    }

    [Fact]
    public void CategorySeatCountDifferentFromCapacity_IsRejected()
    {
        // 20 + 100 seats in total, but VIP gets 30 and General 90.
        var spec = Spec(new SeatLayoutSection("Front", Vip, 3, 10), new SeatLayoutSection("Hall", General, 9, 10));

        var codes = Codes(spec, Categories());

        Assert.Contains("CategoryCapacityMismatch", codes);
        Assert.DoesNotContain("TotalCapacityMismatch", codes);
    }

    [Fact]
    public void CategoryWithoutAnySection_IsRejected()
    {
        var spec = Spec(new SeatLayoutSection("Hall", General, 10, 10));

        Assert.Contains("CategoryCapacityMismatch", Codes(spec, Categories()));
    }

    [Fact]
    public void TotalDifferentFromTicketTotal_IsRejected()
    {
        var categories = Categories();
        categories.Add(new TicketCategory { Id = Guid.NewGuid(), Name = "Extra", Capacity = 0, IsActive = false });
        var spec = Spec(new SeatLayoutSection("Front", Vip, 2, 10), new SeatLayoutSection("Hall", General, 10, 10), new SeatLayoutSection("Hall B", General, 1, 1));

        var codes = Codes(spec, categories);

        Assert.Contains("TotalCapacityMismatch", codes);
    }

    [Fact]
    public void OversizedLayout_IsRejected()
    {
        var spec = Spec(Enumerable.Range(0, 11).Select(i => new SeatLayoutSection($"S{i}", Vip, 200, 500)).ToArray());

        Assert.Contains("TooLarge", Codes(spec, Categories(1_000_000, 1)));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void RowLabels_RunAToZThenAA(int index, string expected) => Assert.Equal(expected, SeatLayoutRules.RowLabel(index));

    [Fact]
    public void Expand_ProducesEveryDistinctSeatOnce()
    {
        var spec = ValidSpec();

        var seats = SeatLayoutRules.Expand(spec).ToList();

        Assert.Equal(120, seats.Count);
        Assert.Equal(SeatLayoutRules.SeatCount(spec), seats.Count);
        Assert.Equal(120, seats.Select(s => (s.Section, s.Row, s.Number)).Distinct().Count());
        Assert.Equal(20, seats.Count(s => s.CategoryId == Vip));
        Assert.Equal(new SeatPosition(Vip, "Front", "A", 1), seats[0]);
        Assert.Equal(new SeatPosition(Vip, "Front", "B", 10), seats[19]);
    }
}
