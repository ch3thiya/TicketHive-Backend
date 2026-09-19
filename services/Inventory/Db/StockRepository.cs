using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public class StockRepository : IStockRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public StockRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<List<StockItem>> InitializeAsync(ShowRules rules, List<StockItem> categories)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        using var transaction = await connection.BeginTransactionAsync();

        const string upsertRulesSql = @"
            INSERT INTO show_rules (show_id, organizer_id, on_sale_at, max_per_customer, hold_minutes, high_demand)
            VALUES (@ShowId, @OrganizerId, @OnSaleAt, @MaxPerCustomer, @HoldMinutes, @HighDemand)
            ON CONFLICT (show_id) DO UPDATE SET
                organizer_id = EXCLUDED.organizer_id,
                on_sale_at = EXCLUDED.on_sale_at,
                max_per_customer = EXCLUDED.max_per_customer,
                hold_minutes = EXCLUDED.hold_minutes,
                high_demand = EXCLUDED.high_demand;
        ";

        using (var rulesCommand = new NpgsqlCommand(upsertRulesSql, connection, transaction))
        {
            rulesCommand.Parameters.AddWithValue("ShowId", rules.ShowId);
            rulesCommand.Parameters.AddWithValue("OrganizerId", rules.OrganizerId);
            rulesCommand.Parameters.AddWithValue("OnSaleAt", (object?)rules.OnSaleAt ?? DBNull.Value);
            rulesCommand.Parameters.AddWithValue("MaxPerCustomer", rules.MaxPerCustomer);
            rulesCommand.Parameters.AddWithValue("HoldMinutes", rules.HoldMinutes);
            rulesCommand.Parameters.AddWithValue("HighDemand", rules.HighDemand);
            await rulesCommand.ExecuteNonQueryAsync();
        }

        // ON CONFLICT DO NOTHING: a repeat call for a show that already has
        // stock must never touch available, since S2-03 holds decrement it.
        const string insertStockSql = @"
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency, allocation_mode)
            VALUES (@ShowId, @CategoryId, @Capacity, @Capacity, @UnitPrice, @Currency, @AllocationMode)
            ON CONFLICT (show_id, category_id) DO NOTHING;
        ";

        // Fixed order avoids deadlocking against a concurrent initialize
        // call for the same show touching the same rows in a different order.
        foreach (var category in categories.OrderBy(c => c.CategoryId))
        {
            using var stockCommand = new NpgsqlCommand(insertStockSql, connection, transaction);
            stockCommand.Parameters.AddWithValue("ShowId", rules.ShowId);
            stockCommand.Parameters.AddWithValue("CategoryId", category.CategoryId);
            stockCommand.Parameters.AddWithValue("Capacity", category.Capacity);
            stockCommand.Parameters.AddWithValue("UnitPrice", category.UnitPrice);
            stockCommand.Parameters.AddWithValue("Currency", category.Currency);
            stockCommand.Parameters.AddWithValue("AllocationMode", category.AllocationMode);
            await stockCommand.ExecuteNonQueryAsync();
        }

        var result = await GetByShowIdAsync(rules.ShowId, connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    public async Task<List<StockItem>> GetByShowIdAsync(Guid showId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        return await GetByShowIdAsync(showId, connection, null);
    }

    private static async Task<List<StockItem>> GetByShowIdAsync(Guid showId, NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        const string sql = @"
            SELECT show_id, category_id, capacity, available, unit_price, currency, allocation_mode
            FROM stock
            WHERE show_id = @ShowId
            ORDER BY category_id;
        ";

        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ShowId", showId);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<StockItem>();
        while (await reader.ReadAsync())
        {
            list.Add(new StockItem
            {
                ShowId = reader.GetGuid(0),
                CategoryId = reader.GetGuid(1),
                Capacity = reader.GetInt32(2),
                Available = reader.GetInt32(3),
                UnitPrice = reader.GetDecimal(4),
                Currency = reader.GetString(5).Trim(),
                AllocationMode = reader.GetString(6)
            });
        }

        return list;
    }
}
