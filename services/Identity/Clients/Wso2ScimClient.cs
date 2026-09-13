using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Identity.Service.Clients;

public class Wso2ScimClient : IWso2ScimClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<Wso2ScimClient> _logger;
    private readonly string _customSchemaUrn;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public Wso2ScimClient(HttpClient httpClient, IConfiguration configuration, ILogger<Wso2ScimClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        // Custom schema URN defined in WSO2/Asgardeo claim mapping
        _customSchemaUrn = configuration["Wso2:CustomSchemaUrn"] 
            ?? "urn:scim:schemas:extension:tickethive:2.0:User";
            
        // M2M client credentials for SCIM authorization in Asgardeo
        _clientId = configuration["Wso2:M2mClientId"] ?? string.Empty;
        _clientSecret = configuration["Wso2:M2mClientSecret"] ?? string.Empty;
    }

    /// <summary>
    /// Obtains an access token via client credentials grant from Asgardeo to authorize SCIM calls.
    /// </summary>
    private async Task<string> GetM2mAccessTokenAsync()
    {
        using var client = new HttpClient();
        
        // Asgardeo token endpoint is baseAddress + oauth2/token
        var tokenUrl = new Uri(_httpClient.BaseAddress!, "oauth2/token");
        
        var requestData = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("scope", "internal_user_mgt_create internal_user_mgt_update internal_user_mgt_delete internal_user_mgt_view")
        };
        
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(requestData)
        };
        
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_clientId}:{_clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to retrieve M2M token from Asgardeo. Status: {Status}, Error: {Error}", response.StatusCode, err);
            throw new Exception($"Failed to obtain M2M access token: {err}");
        }
        
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }



    /// <summary>
    /// Creates a user in WSO2 Asgardeo via SCIM
    /// </summary>
    public async Task<string> CreateUserAsync(string username, string password, string email, string fullName, string initialStatus)
    {
        _logger.LogInformation("Creating user {Username} in Asgardeo via SCIM", username);

        var nameParts = fullName.Split(' ', 2);
        var givenName = nameParts[0];
        var familyName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

        // Target the writeable customer user store (default: DEFAULT)
        var userStoreDomain = "DEFAULT";
        var finalUsername = $"{userStoreDomain}/{username}";

        var requestPayload = new
        {
            schemas = new[]
            {
                "urn:ietf:params:scim:schemas:core:2.0:User",
                _customSchemaUrn
            },
            userName = finalUsername,
            name = new
            {
                familyName = familyName,
                givenName = givenName
            },
            emails = new[]
            {
                new { value = email, primary = true }
            },
            password = password
        };

        var jsonString = JsonSerializer.Serialize(requestPayload);
        using var document = JsonDocument.Parse(jsonString);
        var root = document.RootElement;
        
        var requestDict = new Dictionary<string, object>();
        foreach (var prop in root.EnumerateObject())
        {
            requestDict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        }

        requestDict[_customSchemaUrn] = new Dictionary<string, string>
        {
            { "isapproved", initialStatus }
        };

        var finalJson = JsonSerializer.Serialize(requestDict);
        using var content = new StringContent(finalJson, Encoding.UTF8, "application/json");

        // Authenticate the request using M2M bearer token
        var m2mToken = await GetM2mAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);

        var response = await _httpClient.PostAsync("scim2/Users", content);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Asgardeo user creation failed. Status: {Status}, Error: {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to create user in identity provider: {errorContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseBody);
        var wso2Id = responseDoc.RootElement.GetProperty("id").GetString();
        
        if (string.IsNullOrEmpty(wso2Id))
        {
            throw new Exception("Asgardeo user creation returned empty ID.");
        }

        return wso2Id;
    }

    /// <summary>
    /// Patches the custom isapproved claim attribute for a user.
    /// </summary>
    public async Task UpdateApprovalStatusAsync(string wso2UserId, string newStatus)
    {
        _logger.LogInformation("Updating approval status to {Status} for user {Wso2UserId}", newStatus, wso2UserId);

        var patchRequest = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "replace",
                    value = new Dictionary<string, object>
                    {
                        {
                            _customSchemaUrn, new Dictionary<string, string>
                            {
                                { "isapproved", newStatus }
                            }
                        }
                    }
                }
            }
        };

        var finalJson = JsonSerializer.Serialize(patchRequest);
        using var content = new StringContent(finalJson, Encoding.UTF8, "application/json");

        // Authenticate the request using M2M bearer token
        var m2mToken = await GetM2mAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);

        var response = await _httpClient.PatchAsync($"scim2/Users/{wso2UserId}", content);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Asgardeo user patch failed. Status: {Status}, Error: {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to update user approval status in identity provider: {errorContent}");
        }
    }

    /// <summary>
    /// Deletes a user in WSO2 Asgardeo via SCIM
    /// </summary>
    public async Task DeleteUserAsync(string wso2UserId)
    {
        _logger.LogInformation("Deleting user {Wso2UserId} from Asgardeo via SCIM", wso2UserId);

        var m2mToken = await GetM2mAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);

        var response = await _httpClient.DeleteAsync($"scim2/Users/{wso2UserId}");
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to delete user {Wso2UserId} from Asgardeo. Status: {Status}, Error: {Error}", wso2UserId, response.StatusCode, errorContent);
            throw new Exception($"Failed to delete user from identity provider: {errorContent}");
        }
        
        _logger.LogInformation("Successfully deleted user {Wso2UserId} from Asgardeo via SCIM", wso2UserId);
    }

    /// <summary>
    /// Assigns a user to a Group (Role).
    /// </summary>
    public async Task AssignUserToGroupAsync(string wso2UserId, string username, string groupName)
    {
        _logger.LogInformation("Assigning user {Username} to group {GroupName}", username, groupName);

        var m2mToken = await GetM2mAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);

        var groupId = await GetGroupIdByNameAsync(groupName);
        if (string.IsNullOrEmpty(groupId))
        {
            _logger.LogWarning("Group {GroupName} not found in Asgardeo. Skipping role assignment.", groupName);
            return;
        }

        if (await IsUserMemberOfGroupAsync(groupId, wso2UserId))
        {
            _logger.LogInformation("User {Username} is already a member of group {GroupName}; skipping assignment.", username, groupName);
            return;
        }

        var groupPatch = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "add",
                    path = "members",
                    value = new[]
                    {
                        new
                        {
                            value = wso2UserId,
                            display = username
                        }
                    }
                }
            }
        };

        var finalJson = JsonSerializer.Serialize(groupPatch);
        using var content = new StringContent(finalJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PatchAsync($"scim2/Groups/{groupId}", content);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to add user to group {GroupName}. Status: {Status}, Error: {Error}", groupName, response.StatusCode, errorContent);
            throw new Exception($"Failed to assign group in identity provider: {errorContent}");
        }
    }

    /// <summary>
    /// Checks whether a user is already a member of a group, so assignment can
    /// be skipped instead of relying on how Asgardeo responds to a duplicate add.
    /// </summary>
    private async Task<bool> IsUserMemberOfGroupAsync(string groupId, string wso2UserId)
    {
        var response = await _httpClient.GetAsync($"scim2/Groups/{groupId}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch group {GroupId} to check membership. Status: {Status}", groupId, response.StatusCode);
            return false;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseBody);

        if (!responseDoc.RootElement.TryGetProperty("members", out var membersElement) ||
            membersElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var member in membersElement.EnumerateArray())
        {
            if (member.TryGetProperty("value", out var valueElement) &&
                string.Equals(valueElement.GetString(), wso2UserId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> GetGroupIdByNameAsync(string groupName)
    {
        var response = await _httpClient.GetAsync($"scim2/Groups?filter=displayName eq {Uri.EscapeDataString(groupName)}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to query groups. Status: {Status}", response.StatusCode);
            return null;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseBody);
        
        if (responseDoc.RootElement.TryGetProperty("totalResults", out var totalResultsElement) && 
            totalResultsElement.GetInt32() > 0 && 
            responseDoc.RootElement.TryGetProperty("Resources", out var resourcesElement) && 
            resourcesElement.ValueKind == JsonValueKind.Array && 
            resourcesElement.GetArrayLength() > 0)
        {
            return resourcesElement[0].GetProperty("id").GetString();
        }

        return null;
    }
}
