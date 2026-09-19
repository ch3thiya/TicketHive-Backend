using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Inventory.Service.Db;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public class WaitingRoomService : IWaitingRoomService
{
    private readonly IWaitingRoomRepository _repository;
    private readonly IHoldRepository _holdRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaitingRoomService> _logger;

    public WaitingRoomService(
        IWaitingRoomRepository repository,
        IHoldRepository holdRepository,
        TimeProvider timeProvider,
        ILogger<WaitingRoomService> logger)
    {
        _repository = repository;
        _holdRepository = holdRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private async Task TryAdmitEligibleCustomersAsync(Guid showId, DateTimeOffset now)
    {
        var rules = await _holdRepository.GetShowRulesAsync(showId);
        var threshold = rules?.HighDemandThreshold ?? 1;

        var activeHolds = await _holdRepository.GetTotalActiveHoldsAsync(showId, now);
        var activeAdmissions = await _repository.GetActiveAdmissionsCountAsync(showId, now);

        var occupiedSlots = activeHolds + activeAdmissions;
        var availableSlots = Math.Max(0, threshold - occupiedSlots);

        if (availableSlots > 0)
        {
            await _repository.AdmitNextCustomersAsync(showId, batchSize: availableSlots, tokenDurationMinutes: 10, now);
        }
    }

    public async Task<QueueStatusResponse> JoinQueueAsync(Guid showId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = await _repository.JoinQueueAsync(showId, customerSub, now);

        if (entry.Status == WaitingRoomStatus.Waiting)
        {
            await TryAdmitEligibleCustomersAsync(showId, now);
            var updated = await _repository.GetStatusAsync(showId, customerSub, now);
            if (updated != null) entry = updated;
        }

        var totalWaiting = await _repository.GetTotalWaitingAsync(showId);

        _logger.LogInformation("Customer {CustomerSub} joined/checked waiting room for show {ShowId} with status {Status}",
            customerSub, showId, entry.Status);

        return ToQueueStatusResponse(entry, totalWaiting);
    }

    public async Task<QueueStatusResponse?> GetQueueStatusAsync(Guid showId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = await _repository.GetStatusAsync(showId, customerSub, now);
        if (entry is null) return null;

        if (entry.Status == WaitingRoomStatus.Waiting)
        {
            await TryAdmitEligibleCustomersAsync(showId, now);
            var updated = await _repository.GetStatusAsync(showId, customerSub, now);
            if (updated != null) entry = updated;
        }

        var totalWaiting = await _repository.GetTotalWaitingAsync(showId);
        return ToQueueStatusResponse(entry, totalWaiting);
    }

    public async Task<AdmitCustomersResponse> AdmitNextCustomersAsync(Guid showId, int batchSize = 10, int tokenDurationMinutes = 10)
    {
        var now = _timeProvider.GetUtcNow();
        var admitted = await _repository.AdmitNextCustomersAsync(showId, batchSize, tokenDurationMinutes, now);

        _logger.LogInformation("Admitted {Count} customers to show {ShowId}", admitted.Count, showId);

        return new AdmitCustomersResponse(
            admitted.Count,
            admitted.Select(a => a.CustomerSub).ToList());
    }

    public Task<bool> ValidateAdmissionTokenAsync(Guid showId, string customerSub, string admissionToken)
    {
        var now = _timeProvider.GetUtcNow();
        return _repository.ValidateAdmissionTokenAsync(showId, customerSub, admissionToken, now);
    }

    private static QueueStatusResponse ToQueueStatusResponse(WaitingRoomEntry entry, int totalWaiting)
    {
        return new QueueStatusResponse(
            entry.ShowId,
            entry.CustomerSub,
            entry.Status.ToString(),
            entry.Position,
            totalWaiting,
            entry.AdmissionToken,
            entry.TokenExpiresAt);
    }
}
