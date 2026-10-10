using Microsoft.Extensions.Logging;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public class EntryAccessService : IEntryAccessService
{
    private readonly IEventRepository _repository;
    private readonly IOrganizerStatusClient _organizerStatusClient;
    private readonly ILogger<EntryAccessService> _logger;

    public EntryAccessService(
        IEventRepository repository,
        IOrganizerStatusClient organizerStatusClient,
        ILogger<EntryAccessService> logger)
    {
        _repository = repository;
        _organizerStatusClient = organizerStatusClient;
        _logger = logger;
    }

    public async Task<EntryAccessResult> CheckAsync(Guid showId, string sub, CancellationToken cancellationToken = default)
    {
        var show = await _repository.GetShowByIdAsync(showId);
        var evt = show is null ? null : await _repository.GetEventByIdAsync(show.EventId);
        if (show is null || evt is null)
        {
            return new EntryAccessResult(showId, EntryAccessOutcome.ShowNotFound);
        }

        if (string.IsNullOrWhiteSpace(sub))
        {
            return new EntryAccessResult(showId, EntryAccessOutcome.NotAnOrganizer);
        }

        var organizer = await _organizerStatusClient.GetOrganizerStatusAsync(sub, cancellationToken);
        var outcome = organizer.Status switch
        {
            // Active and Suspended organizers both own their shows; suspension never stops entry.
            OrganizerLookupStatus.Active or OrganizerLookupStatus.Suspended =>
                organizer.OrganizerId == evt.OrganizerId ? EntryAccessOutcome.Allowed : EntryAccessOutcome.NotShowOwner,
            OrganizerLookupStatus.NotFound => EntryAccessOutcome.NotAnOrganizer,
            _ => EntryAccessOutcome.OrganizerStatusUnavailable
        };

        if (outcome != EntryAccessOutcome.Allowed)
        {
            _logger.LogInformation("Entry access to show {ShowId} refused: {Outcome}", showId, outcome);
        }

        return new EntryAccessResult(showId, outcome);
    }
}