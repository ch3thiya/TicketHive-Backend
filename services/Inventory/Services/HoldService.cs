using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Inventory.Service.Db;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public class HoldService : IHoldService
{
    private readonly IHoldRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HoldService> _logger;

    public HoldService(IHoldRepository repository, TimeProvider timeProvider, ILogger<HoldService> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CreateHoldResult> CreateHoldAsync(string customerSub, string idempotencyKey, bool hasAdmissionToken, CreateHoldRequest request)
    {
        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one item is required to place a hold.");
        }

        foreach (var item in request.Items)
        {
            if (item.Quantity <= 0)
            {
                throw new ArgumentException($"Item quantity must be positive. Invalid quantity: {item.Quantity}");
            }
        }

        var showRules = await _repository.GetShowRulesAsync(request.ShowId);
        if (showRules is null)
        {
            return new CreateHoldResult { Status = CreateHoldStatus.ShowNotFound };
        }

        // Fails closed before any stock is touched: no show is high-demand
        // today, and this stays refused until S2-05's admission tokens can
        // be verified for real.
        if (showRules.HighDemand && !hasAdmissionToken)
        {
            _logger.LogInformation("Hold rejected for show {ShowId}: high-demand show with no admission token", request.ShowId);
            return new CreateHoldResult { Status = CreateHoldStatus.HighDemandBlocked };
        }

        var now = _timeProvider.GetUtcNow();
        var hold = new Hold
        {
            Id = Guid.CreateVersion7(),
            ShowId = request.ShowId,
            CustomerSub = customerSub,
            Status = HoldStatus.Active,
            ExpiresAt = now.AddMinutes(showRules.HoldMinutes),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            Items = request.Items
                .Select(i => new HoldItem { CategoryId = i.CategoryId, Quantity = i.Quantity })
                .ToList()
        };

        var result = await _repository.CreateAsync(hold, showRules.MaxPerCustomer);

        switch (result.Outcome)
        {
            case HoldCreationOutcome.Created:
                _logger.LogInformation("Hold {HoldId} created for show {ShowId}", hold.Id, hold.ShowId);
                return new CreateHoldResult { Status = CreateHoldStatus.Created, Hold = ToResponse(result.Hold!) };
            case HoldCreationOutcome.Duplicate:
                return new CreateHoldResult { Status = CreateHoldStatus.Duplicate, Hold = ToResponse(result.Hold!) };
            case HoldCreationOutcome.QuotaExceeded:
                _logger.LogInformation("Hold rejected for show {ShowId}: customer quota would exceed {Limit}", request.ShowId, result.Limit);
                return new CreateHoldResult { Status = CreateHoldStatus.QuotaExceeded, Limit = result.Limit };
            case HoldCreationOutcome.CategoryNotFound:
                return new CreateHoldResult { Status = CreateHoldStatus.CategoryNotFound, CategoryId = result.CategoryId };
            case HoldCreationOutcome.StockUnavailable:
                _logger.LogInformation("Hold rejected for show {ShowId}: category {CategoryId} sold out", request.ShowId, result.CategoryId);
                return new CreateHoldResult { Status = CreateHoldStatus.StockUnavailable, CategoryId = result.CategoryId };
            default:
                throw new InvalidOperationException($"Unhandled hold creation outcome '{result.Outcome}'.");
        }
    }

    public Task<Hold?> GetHoldAsync(Guid holdId) => _repository.GetByIdAsync(holdId);

    public static HoldResponse ToResponse(Hold hold) => new(
        hold.Id,
        hold.ShowId,
        hold.Status.ToString(),
        hold.ExpiresAt,
        hold.Items.Select(i => new HoldItemResponse(i.CategoryId, i.Quantity, i.UnitPrice, i.Currency)).ToList());
}
