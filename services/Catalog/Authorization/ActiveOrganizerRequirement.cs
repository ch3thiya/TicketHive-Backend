using Microsoft.AspNetCore.Authorization;

namespace Catalog.Service.Authorization;

public class ActiveOrganizerRequirement : IAuthorizationRequirement
{
    public ActiveOrganizerRequirement(bool allowSuspended = false)
    {
        AllowSuspended = allowSuspended;
    }

    /// <summary>
    /// When true a suspended organizer is still let through (read-only endpoints). Management
    /// endpoints leave this false so a suspended organizer is refused with 403.
    /// </summary>
    public bool AllowSuspended { get; }
}