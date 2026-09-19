using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Inventory.Service.Db;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public class StockService : IStockService
{
    private readonly IStockRepository _repository;
    private readonly ILogger<StockService> _logger;

    public StockService(IStockRepository repository, ILogger<StockService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<List<StockItemResponse>> InitializeAsync(Guid showId, InitializeStockRequest request)
    {
        if (request.Categories == null || request.Categories.Count == 0)
        {
            throw new ArgumentException("At least one category is required to initialize stock.");
        }

        foreach (var category in request.Categories)
        {
            if (category.Capacity <= 0)
            {
                throw new ArgumentException($"Category capacity must be positive. Invalid capacity: {category.Capacity}");
            }

            if (category.UnitPrice < 0)
            {
                throw new ArgumentException($"Category unit price must be non-negative. Invalid price: {category.UnitPrice}");
            }

            if (string.IsNullOrWhiteSpace(category.Currency) || category.Currency.Length != 3)
            {
                throw new ArgumentException($"Currency must be a three-character code. Invalid currency: '{category.Currency}'");
            }
        }

        var rules = new ShowRules
        {
            ShowId = showId,
            OrganizerId = request.OrganizerId,
            OnSaleAt = request.OnSaleAt,
            MaxPerCustomer = request.MaxPerCustomer,
            HoldMinutes = request.HoldMinutes,
            HighDemand = request.HighDemand
        };

        var stockItems = request.Categories.Select(c => new StockItem
        {
            ShowId = showId,
            CategoryId = c.CategoryId,
            Capacity = c.Capacity,
            UnitPrice = c.UnitPrice,
            Currency = c.Currency,
            AllocationMode = c.AllocationMode
        }).ToList();

        var result = await _repository.InitializeAsync(rules, stockItems);
        _logger.LogInformation("Initialized stock for show {ShowId} with {CategoryCount} categories", showId, result.Count);

        return result.Select(ToResponse).ToList();
    }

    public async Task<List<StockItemResponse>?> GetAvailabilityAsync(Guid showId)
    {
        var stock = await _repository.GetByShowIdAsync(showId);
        if (stock.Count == 0)
        {
            return null;
        }

        return stock.Select(ToResponse).ToList();
    }

    private static StockItemResponse ToResponse(StockItem item) =>
        new(item.CategoryId, item.Capacity, item.Available, item.UnitPrice, item.Currency);
}
