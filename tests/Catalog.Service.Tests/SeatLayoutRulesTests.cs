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
    [InlineData("", 1, 1)]
    [InlineData("Front", 0, 10)]
    [InlineData("Front", 10, 0)]
    [InlineData("Front", -1, 10)]
    [InlineData("Front", 201, 10)]
    [InlineData("Front", 10, 501)]
    public void BadNamesOrDimensions_AreRejected(string name, int rows, int seats)
    {
        var spec = Spec(new SeatLayoutSection(name, Vip, rows, seats));

        Assert.NotEmpty(Codes(spec, Categories()));
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
