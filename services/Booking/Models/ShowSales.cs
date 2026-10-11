namespace Booking.Service.Models;

// One category's confirmed sales, straight from the order items.
public record SoldRow(Guid CategoryId, string Currency, int Sold, decimal Revenue);

// Validated (used), non-voided tickets of confirmed orders for one category.
public record UsedRow(Guid CategoryId, int Used);

public record SalesCategoryLine(Guid CategoryId, int Sold, decimal Revenue, int Used);

public record ShowSales(
    Guid ShowId,
    string Currency,
    int TicketsSold,
    decimal Revenue,
    int TicketsUsed,
    IReadOnlyList<SalesCategoryLine> Categories,
    DateTimeOffset AsOf);

public enum SalesOutcome
{
    Ok,
    MixedCurrencies
}

public record SalesResult(SalesOutcome Outcome, ShowSales? Sales);

/// <summary>
/// Combines a show's sold and used rows into its sales figures. Revenue is never added across
/// currencies: a show whose confirmed orders are in more than one currency has no single total.
/// </summary>
public static class ShowSalesCalculator
{
    public const string DefaultCurrency = "LKR";

    public static SalesResult Compose(Guid showId, IReadOnlyList<SoldRow> sold, IReadOnlyList<UsedRow> used, DateTimeOffset asOf)
    {
        var currencies = sold.Select(s => s.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
        {
            return new SalesResult(SalesOutcome.MixedCurrencies, null);
        }

        var usedByCategory = used.GroupBy(u => u.CategoryId).ToDictionary(g => g.Key, g => g.Sum(u => u.Used));
        var lines = sold
            .GroupBy(s => s.CategoryId)
            .Select(g => new SalesCategoryLine(g.Key, g.Sum(s => s.Sold), g.Sum(s => s.Revenue), usedByCategory.GetValueOrDefault(g.Key)))
            .OrderBy(l => l.CategoryId)
            .ToList();

        var sales = new ShowSales(
            showId,
            currencies.SingleOrDefault() ?? DefaultCurrency,
            lines.Sum(l => l.Sold),
            lines.Sum(l => l.Revenue),
            usedByCategory.Values.Sum(),
            lines,
            asOf);
        return new SalesResult(SalesOutcome.Ok, sales);
    }
}
