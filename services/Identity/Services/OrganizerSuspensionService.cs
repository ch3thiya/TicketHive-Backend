using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Services;

public class OrganizerSuspensionService : IOrganizerSuspensionService
{
    public const int MaxReasonLength = 500;
    public const string DefaultReinstatementNote = "Reinstated by an administrator.";

    private readonly IAccountRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrganizerSuspensionService> _logger;

    public OrganizerSuspensionService(
        IAccountRepository repository,
        TimeProvider timeProvider,
        ILogger<OrganizerSuspensionService> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OrganizerStatusChangeResult> SuspendAsync(Guid organizerId, string actorSub, string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReasonLength)
        {
            return new OrganizerStatusChangeResult(OrganizerStatusChangeOutcome.InvalidReason, null);
        }

        return await ApplyAsync(organizerId, OrganizerStatusAction.Suspend, actorSub, trimmed);
    }

    public async Task<OrganizerStatusChangeResult> ReinstateAsync(Guid organizerId, string actorSub, string? note)
    {
        var trimmed = note?.Trim();
        if (trimmed is { Length: > MaxReasonLength })
        {
            return new OrganizerStatusChangeResult(OrganizerStatusChangeOutcome.InvalidReason, null);
        }

        return await ApplyAsync(
            organizerId, OrganizerStatusAction.Reinstate, actorSub,
            string.IsNullOrEmpty(trimmed) ? DefaultReinstatementNote : trimmed);
    }

    public async Task<List<OrganizerStatusAuditEntry>?> GetHistoryAsync(Guid organizerId)
    {
        var account = await _repository.GetUserAccountByIdAsync(organizerId);
        if (account is null || account.Role != "Organizer")
        {
            return null;
        }

        return await _repository.GetOrganizerStatusHistoryAsync(organizerId);
    }

    private async Task<OrganizerStatusChangeResult> ApplyAsync(
        Guid organizerId, OrganizerStatusAction action, string actorSub, string reason)
    {
        var result = await _repository.ApplyOrganizerStatusChangeAsync(
            organizerId, action, actorSub, reason, _timeProvider.GetUtcNow());

        _logger.LogInformation(
            "Admin {ActorSub} requested {Action} for organizer {OrganizerId}; outcome {Outcome}",
            actorSub, action, organizerId, result.Outcome);

        return result;
    }
}
