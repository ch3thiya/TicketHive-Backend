using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Service.Models;

namespace Identity.Service.Services;

public interface IOrganizerSuspensionService
{
    Task<OrganizerStatusChangeResult> SuspendAsync(Guid organizerId, string actorSub, string? reason);
    Task<OrganizerStatusChangeResult> ReinstateAsync(Guid organizerId, string actorSub, string? note);
    Task<List<OrganizerStatusAuditEntry>?> GetHistoryAsync(Guid organizerId);
}
