using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public interface IStockRepository
{
    // Upserts show_rules and inserts a stock row per category (ON CONFLICT DO
    // NOTHING on stock, so a repeat call never resets an existing row's
    // available column), all in one transaction, then returns the current
    // stock rows for the show — reflecting whatever is actually in the
    // database, not necessarily the values just passed in.
    Task<List<StockItem>> InitializeAsync(ShowRules rules, List<StockItem> categories);

    // Returns the current stock rows for a show, or an empty list if the
    // show has never been initialized.
    Task<List<StockItem>> GetByShowIdAsync(Guid showId);
}
