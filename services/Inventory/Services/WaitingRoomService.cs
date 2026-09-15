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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaitingRoomService> _logger;

    public WaitingRoomService(
        IWaitingRoomRepository repository,
        TimeProvider timeProvider,
        ILogger<WaitingRoomService> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<QueueStatusResponse> JoinQueueAsync(Guid showId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = await _repository.JoinQueueAsync(showId, customerSub, now);
        var totalWaiting = await _repository.GetTotalWaitingAsync(showId);

        _logger.LogInformation("Customer {CustomerSub} joined waiting room for show {ShowId} at position {Position}",
            customerSub, showId, entry.Position);

        return ToQueueStatusResponse(entry, totalWaiting);
    }

    public async Task<QueueStatusResponse?> GetQueueStatusAsync(Guid showId, string customerSub)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = await _repository.GetStatusAsync(showId, customerSub, now);
        if (entry is null) return null;

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
