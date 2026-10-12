using Microsoft.Extensions.Logging;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public class SalesEligibilityService : ISalesEligibilityService
{
    private readonly IEventRepository _repository;
    private readonly IOrganizerStatusClient _organizerStatusClient;
    private readonly ILogger<SalesEligibilityService> _logger;

    public SalesEligibilityService(
        IEventRepository repository,
        IOrganizerStatusClient organizerStatusClient,
        ILogger<SalesEligibilityService> logger)
    {
        _repository = repository;
        _organizerStatusClient = organizerStatusClient;
        _logger = logger;
    }

    public async Task<SalesEligibilityResult> CheckAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        var show = await _repository.GetShowByIdAsync(showId);
        if (show is null)
        {
            return new SalesEligibilityResult(showId, SalesEligibilityOutcome.ShowNotFound);
        }

        var evt = await _repository.GetEventByIdAsync(show.EventId);
        if (evt is null)
        {
            return new SalesEligibilityResult(showId, SalesEligibilityOutcome.ShowNotFound);
        }

        if (!SalesEligibilityRules.IsShowSellable(evt.Status, show.Status))
        {
            return new SalesEligibilityResult(showId, SalesEligibilityOutcome.ShowNotOnSale);
        }

        var organizer = await _organizerStatusClient.GetOrganizerStatusByIdAsync(evt.OrganizerId, cancellationToken);
        var outcome = organizer.Status switch
        {
            OrganizerLookupStatus.Active => SalesEligibilityOutcome.Eligible,
            OrganizerLookupStatus.Suspended => SalesEligibilityOutcome.OrganizerSuspended,
            OrganizerLookupStatus.NotFound => SalesEligibilityOutcome.OrganizerNotActive,
            _ => SalesEligibilityOutcome.OrganizerStatusUnavailable
        };

        if (outcome != SalesEligibilityOutcome.Eligible)
        {
            _logger.LogInformation("Show {ShowId} is not eligible for sales: {Outcome}", showId, outcome);
        }

        return new SalesEligibilityResult(showId, outcome);
    }

    public async Task<IReadOnlySet<Guid>> GetSuspendedOrganizerIdsAsync(IEnumerable<Guid> organizerIds, CancellationToken cancellationToken = default)
    {
        var ids = organizerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var statuses = await _organizerStatusClient.GetOrganizerStatusesAsync(ids, cancellationToken);
        if (statuses is null)
        {
            return new HashSet<Guid>();
        }

        return statuses.Where(s => s.Value == OrganizerLookupStatus.Suspended).Select(s => s.Key).ToHashSet();
    }
}
