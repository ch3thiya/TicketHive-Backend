using System.Threading.Tasks;

namespace Identity.Service.Clients;

public interface IWso2ScimClient
{
    Task<string> CreateUserAsync(string username, string password, string email, string fullName, string initialStatus);
    Task UpdateApprovalStatusAsync(string wso2UserId, string newStatus);
    Task DeleteUserAsync(string wso2UserId);
    Task AssignUserToGroupAsync(string wso2UserId, string username, string groupName);
}
