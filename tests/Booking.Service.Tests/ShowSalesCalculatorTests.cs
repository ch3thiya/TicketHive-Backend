using Booking.Service.Models;
using Xunit;

namespace Booking.Service.Tests;

public class ShowSalesCalculatorTests
{
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void NoSales_IsZerosInTheDefaultCurrency()
    {
        var result = ShowSalesCalculator.Compose(Show, Array.Empty<SoldRow>(), Array.Empty<UsedRow>(), Now);

        Assert.Equal(SalesOutcome.Ok, result.Outcome);
        Assert.Equal((0, 0m, 0, "LKR"), (result.Sales!.TicketsSold, result.Sales.Revenue, result.Sales.TicketsUsed, result.Sales.Currency));
        Assert.Empty(result.Sales.Categories);
    }

    [Fact]
    public void TotalsAreTheSumOfTheCategoryLines_InAStableOrder()
    {
        var sold = new[] { new SoldRow(B, "LKR", 3, 90m), new SoldRow(A, "LKR", 2, 200m) };
        var used = new[] { new UsedRow(A, 1), new UsedRow(B, 2) };

        var sales = ShowSalesCalculator.Compose(Show, sold, used, Now).Sales!;

        Assert.Equal((5, 290m, 3), (sales.TicketsSold, sales.Revenue, sales.TicketsUsed));
        Assert.Equal(new[] { A, B }, sales.Categories.Select(c => c.CategoryId));
        Assert.Equal(new[] { 1, 2 }, sales.Categories.Select(c => c.Used));
    }

    [Fact]
    public void RowsOfOneCategoryInTheSameCurrency_AreMerged()
    {
        var sold = new[] { new SoldRow(A, "LKR", 1, 10m), new SoldRow(A, "LKR", 2, 20m) };

        var line = Assert.Single(ShowSalesCalculator.Compose(Show, sold, Array.Empty<UsedRow>(), Now).Sales!.Categories);

        Assert.Equal((3, 30m), (line.Sold, line.Revenue));
    }

    [Fact]
    public void UsedRowsOfOneCategory_AreSummedRatherThanTakingOneRow()
    {
        var sold = new[] { new SoldRow(A, "LKR", 3, 30m) };
        var used = new[] { new UsedRow(A, 1), new UsedRow(A, 2) };

        var sales = ShowSalesCalculator.Compose(Show, sold, used, Now).Sales!;

        Assert.Equal(3, sales.TicketsUsed);
        Assert.Equal(3, Assert.Single(sales.Categories).Used);
    }

    [Fact]
    public void MoreThanOneCurrency_HasNoTotal()
    {
        var sold = new[] { new SoldRow(A, "LKR", 1, 10m), new SoldRow(B, "USD", 1, 1m) };

        var result = ShowSalesCalculator.Compose(Show, sold, Array.Empty<UsedRow>(), Now);

        Assert.Equal(SalesOutcome.MixedCurrencies, result.Outcome);
        Assert.Null(result.Sales);
    }

    [Fact]
    public void TheShowsCurrencyComesFromItsOrders()
    {
        var sales = ShowSalesCalculator.Compose(Show, new[] { new SoldRow(A, "USD", 1, 5m) }, Array.Empty<UsedRow>(), Now).Sales!;

        Assert.Equal("USD", sales.Currency);
    }
}
