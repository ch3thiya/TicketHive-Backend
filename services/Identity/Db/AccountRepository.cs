using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;
using Identity.Service.Models;

namespace Identity.Service.Db;

public class AccountRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public AccountRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateUserAccountAsync(UserAccount account)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            INSERT INTO user_accounts (id, wso2_sub, email, full_name, role, approval_status)
            VALUES (@Id, @Wso2Sub, @Email, @FullName, @Role, @ApprovalStatus);
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", account.Id);
        command.Parameters.AddWithValue("Wso2Sub", account.Wso2Sub);
        command.Parameters.AddWithValue("Email", account.Email);
        command.Parameters.AddWithValue("FullName", account.FullName);
        command.Parameters.AddWithValue("Role", account.Role);
        command.Parameters.AddWithValue("ApprovalStatus", account.ApprovalStatus);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<UserAccount?> GetUserAccountBySubAsync(string wso2Sub)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT id, wso2_sub, email, full_name, role, approval_status, created_at
            FROM user_accounts
            WHERE wso2_sub = @Wso2Sub;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Wso2Sub", wso2Sub);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserAccount
            {
                Id = reader.GetGuid(0),
                Wso2Sub = reader.GetString(1),
                Email = reader.GetString(2),
                FullName = reader.GetString(3),
                Role = reader.GetString(4),
                ApprovalStatus = reader.GetString(5),
                CreatedAt = reader.GetDateTime(6)
            };
        }

        return null;
    }

    public async Task<UserAccount?> GetUserAccountByIdAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT id, wso2_sub, email, full_name, role, approval_status, created_at
            FROM user_accounts
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserAccount
            {
                Id = reader.GetGuid(0),
                Wso2Sub = reader.GetString(1),
                Email = reader.GetString(2),
                FullName = reader.GetString(3),
                Role = reader.GetString(4),
                ApprovalStatus = reader.GetString(5),
                CreatedAt = reader.GetDateTime(6)
            };
        }

        return null;
    }

    public async Task UpdateUserAccountRoleAndStatusAsync(Guid id, string role, string status)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            UPDATE user_accounts
            SET role = @Role, approval_status = @ApprovalStatus
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);
        command.Parameters.AddWithValue("Role", role);
        command.Parameters.AddWithValue("ApprovalStatus", status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task CreateOrganizerRequestAsync(OrganizerRequest request)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            INSERT INTO organizer_requests (id, user_account_id, organization_name, business_email, phone, event_type, about, status)
            VALUES (@Id, @UserAccountId, @OrgName, @BusinessEmail, @Phone, @EventType, @About, @Status);
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", request.Id);
        command.Parameters.AddWithValue("UserAccountId", request.UserAccountId);
        command.Parameters.AddWithValue("OrgName", request.OrganizationName);
        command.Parameters.AddWithValue("BusinessEmail", request.BusinessEmail);
        command.Parameters.AddWithValue("Phone", request.Phone);
        command.Parameters.AddWithValue("EventType", request.EventType);
        command.Parameters.AddWithValue("About", request.About);
        command.Parameters.AddWithValue("Status", request.Status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Dictionary<string, object>>> GetPendingOrganizerRequestsAsync()
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT r.id, r.user_account_id, r.organization_name, r.business_email, r.phone, r.event_type, r.about, r.status, r.created_at, u.full_name, u.email
            FROM organizer_requests r
            INNER JOIN user_accounts u ON r.user_account_id = u.id
            WHERE r.status = 'pending';
        ";

        using var command = new NpgsqlCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();
        
        var list = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object>
            {
                { "requestId", reader.GetGuid(0) },
                { "accountId", reader.GetGuid(1) },
                { "organizationName", reader.GetString(2) },
                { "businessEmail", reader.GetString(3) },
                { "phone", reader.GetString(4) },
                { "eventType", reader.GetString(5) },
                { "about", reader.GetString(6) },
                { "status", reader.GetString(7) },
                { "createdAt", reader.GetDateTime(8) },
                { "fullName", reader.GetString(9) },
                { "userEmail", reader.GetString(10) }
            };
            list.Add(dict);
        }

        return list;
    }

    public async Task<OrganizerRequest?> GetOrganizerRequestByIdAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT id, user_account_id, organization_name, business_email, phone, event_type, about, status, created_at, reviewed_at
            FROM organizer_requests
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new OrganizerRequest
            {
                Id = reader.GetGuid(0),
                UserAccountId = reader.GetGuid(1),
                OrganizationName = reader.GetString(2),
                BusinessEmail = reader.GetString(3),
                Phone = reader.GetString(4),
                EventType = reader.GetString(5),
                About = reader.GetString(6),
                Status = reader.GetString(7),
                CreatedAt = reader.GetDateTime(8),
                ReviewedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9)
            };
        }

        return null;
    }

    public async Task UpdateOrganizerRequestStatusAsync(Guid id, string status)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            UPDATE organizer_requests
            SET status = @Status, reviewed_at = CURRENT_TIMESTAMP
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);
        command.Parameters.AddWithValue("Status", status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteUserAccountAsync(Guid accountId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        // 1. Delete associated organizer requests
        const string deleteRequestsSql = @"
            DELETE FROM organizer_requests
            WHERE user_account_id = @UserAccountId;
        ";
        using (var commandReq = new NpgsqlCommand(deleteRequestsSql, connection))
        {
            commandReq.Parameters.AddWithValue("UserAccountId", accountId);
            await commandReq.ExecuteNonQueryAsync();
        }

        // 2. Delete the user account record
        const string deleteAccountSql = @"
            DELETE FROM user_accounts
            WHERE id = @Id;
        ";
        using (var commandAcc = new NpgsqlCommand(deleteAccountSql, connection))
        {
            commandAcc.Parameters.AddWithValue("Id", accountId);
            await commandAcc.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<Dictionary<string, object>>> GetApprovedOrganizersAsync()
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT ua.id, ua.email, ua.full_name, ua.created_at, oreq.organization_name, oreq.business_email, oreq.event_type
            FROM user_accounts ua
            LEFT JOIN organizer_requests oreq ON ua.id = oreq.user_account_id
            WHERE ua.role = 'Organizer' AND ua.approval_status = 'approved';
        ";

        using var command = new NpgsqlCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();
        
        var list = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            list.Add(new Dictionary<string, object>
            {
                { "accountId", reader.GetGuid(0) },
                { "email", reader.GetString(1) },
                { "fullName", reader.GetString(2) },
                { "createdAt", reader.GetDateTime(3) },
                { "organizationName", reader.IsDBNull(4) ? "" : reader.GetString(4) },
                { "businessEmail", reader.IsDBNull(5) ? "" : reader.GetString(5) },
                { "eventType", reader.IsDBNull(6) ? "" : reader.GetString(6) }
            });
        }
        
        return list;
    }

    public async Task<UserAccount?> GetUserAccountByEmailAsync(string email)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        
        const string sql = @"
            SELECT id, wso2_sub, email, full_name, role, approval_status, created_at
            FROM user_accounts
            WHERE email = @Email;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Email", email);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserAccount
            {
                Id = reader.GetGuid(0),
                Wso2Sub = reader.GetString(1),
                Email = reader.GetString(2),
                FullName = reader.GetString(3),
                Role = reader.GetString(4),
                ApprovalStatus = reader.GetString(5),
                CreatedAt = reader.GetDateTime(6)
            };
        }

        return null;
    }
}
