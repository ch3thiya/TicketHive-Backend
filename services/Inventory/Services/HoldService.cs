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
    private readonly IAdmissionTokenVerifier _admissionTokenVerifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HoldService> _logger;

    public HoldService(
        IHoldRepository repository,
        TimeProvider timeProvider,
        ILogger<HoldService> logger,
        IAdmissionTokenVerifier admissionTokenVerifier)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
        _admissionTokenVerifier = admissionTokenVerifier;
    }

    public async Task<CreateHoldResult> CreateHoldAsync(string customerSub, string idempotencyKey, bool hasAdmissionToken, CreateHoldRequest request, string? admissionToken = null)
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

        var now = _timeProvider.GetUtcNow();

        bool isHighDemand = showRules.HighDemand;
        bool hasThreshold = showRules.HighDemandThreshold.HasValue && showRules.HighDemandThreshold.Value > 0;

        if (isHighDemand || hasThreshold)
        {
            var activeHoldsCount = await _repository.GetTotalActiveHoldsAsync(request.ShowId, now);
            var threshold = showRules.HighDemandThreshold;

            bool requiresAdmissionToken = hasThreshold
                ? activeHoldsCount >= threshold!.Value
                : isHighDemand;

            if (requiresAdmissionToken)
            {
                if (!hasAdmissionToken || string.IsNullOrWhiteSpace(admissionToken))
                {
                    _logger.LogInformation("Hold rejected for show {ShowId}: high-demand gate active and no admission token provided", request.ShowId);
                    return new CreateHoldResult { Status = CreateHoldStatus.HighDemandBlocked };
                }

                if (!_admissionTokenVerifier.Verify(request.ShowId, customerSub, admissionToken))
                {
                    _logger.LogInformation("Hold rejected for show {ShowId}: admission token failed verification", request.ShowId);
                    return new CreateHoldResult { Status = CreateHoldStatus.HighDemandBlocked };
                }
            }
        }
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

    public Task<bool> CancelHoldAsync(Guid holdId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        return _repository.CancelHoldAsync(holdId, customerSub, now);
    }

    public Task<Hold?> GetActiveHoldForCustomerAsync(Guid showId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        return _repository.GetActiveHoldForCustomerAsync(showId, customerSub, now);
    }

    public Task<bool> FreezeHoldAsync(Guid holdId)
    {
        var now = _timeProvider.GetUtcNow();
        return _repository.FreezeHoldAsync(holdId, now);
    }

    public Task<bool> ConvertHoldAsync(Guid holdId) => _repository.ConvertHoldAsync(holdId);

    public Task<bool> ReleaseHoldAsync(Guid holdId) => _repository.ReleaseHoldAsync(holdId);

    public HoldResponse ToResponse(Hold hold) => new(
        hold.Id,
        hold.ShowId,
        hold.EffectiveStatus(_timeProvider.GetUtcNow()).ToString(),
        hold.ExpiresAt,
        hold.Items.Select(i => new HoldItemResponse(i.CategoryId, i.Quantity, i.UnitPrice, i.Currency)).ToList());

    public InternalHoldResponse ToInternalResponse(Hold hold) => new(
        hold.Id,
        hold.ShowId,
        hold.CustomerSub,
        hold.EffectiveStatus(_timeProvider.GetUtcNow()).ToString(),
        hold.ExpiresAt,
        hold.Items.Select(i => new HoldItemResponse(i.CategoryId, i.Quantity, i.UnitPrice, i.Currency)).ToList());
}
