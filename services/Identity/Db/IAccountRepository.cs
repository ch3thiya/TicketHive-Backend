using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Service.Models;

namespace Identity.Service.Db;

public interface IAccountRepository
{
    Task CreateUserAccountAsync(UserAccount account);
    Task<UserAccount?> GetUserAccountBySubAsync(string wso2Sub);
    Task<UserAccount?> GetUserAccountByIdAsync(Guid id);
    Task<UserAccount?> GetUserAccountByEmailAsync(string email);
    Task UpdateUserAccountRoleAndStatusAsync(Guid id, string role, string status);
    Task CreateOrganizerRequestAsync(OrganizerRequest request);
    Task<List<Dictionary<string, object>>> GetPendingOrganizerRequestsAsync();
    Task<OrganizerRequest?> GetOrganizerRequestByIdAsync(Guid id);
    Task UpdateOrganizerRequestStatusAsync(Guid id, string status);
    Task DeleteUserAccountAsync(Guid accountId);
    Task<List<Dictionary<string, object>>> GetApprovedOrganizersAsync();
}
