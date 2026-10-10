using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;
using Identity.Service.Models;

namespace Identity.Service.Db;

public class AccountRepository : IAccountRepository
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

    public async Task<List<Dictionary<string, object>>> GetApprovedOrganizersAsync()
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT ua.id, ua.email, ua.full_name, ua.created_at, oreq.organization_name, oreq.business_email,
                   oreq.event_type, ua.approval_status, susp.occurred_at, susp.reason
            FROM user_accounts ua
            LEFT JOIN organizer_requests oreq ON ua.id = oreq.user_account_id
            LEFT JOIN LATERAL (
                SELECT occurred_at, reason
                FROM organizer_status_audit
                WHERE organizer_id = ua.id AND action = 'Suspended'
                ORDER BY occurred_at DESC, id DESC
                LIMIT 1
            ) susp ON ua.approval_status = 'suspended'
            WHERE ua.role = 'Organizer' AND ua.approval_status IN ('approved', 'suspended');
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
                { "eventType", reader.IsDBNull(6) ? "" : reader.GetString(6) },
                { "status", reader.GetString(7) },
                { "suspendedAt", reader.IsDBNull(8) ? null! : reader.GetFieldValue<DateTimeOffset>(8) },
                { "suspensionReason", reader.IsDBNull(9) ? null! : reader.GetString(9) }
            });
        }

        return list;
    }

    public async Task<OrganizerStatusChangeResult> ApplyOrganizerStatusChangeAsync(
        Guid organizerId, OrganizerStatusAction action, string actorSub, string reason, DateTimeOffset occurredAt)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // The row lock serialises concurrent suspend/reinstate calls for one organizer.
        string role;
        string current;
        await using (var read = new NpgsqlCommand(
            "SELECT role, approval_status FROM user_accounts WHERE id = @Id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("Id", organizerId);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new OrganizerStatusChangeResult(OrganizerStatusChangeOutcome.NotFound, null);
            }

            role = reader.GetString(0);
            current = reader.GetString(1);
        }

        if (role != "Organizer")
        {
            return new OrganizerStatusChangeResult(OrganizerStatusChangeOutcome.NotFound, null);
        }

        var decision = OrganizerStatusRules.Decide(current, action);
        if (decision.Outcome != OrganizerStatusChangeOutcome.Changed)
        {
            return new OrganizerStatusChangeResult(decision.Outcome, decision.TargetStatus ?? current);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE user_accounts SET approval_status = @Status WHERE id = @Id", connection, transaction))
        {
            update.Parameters.AddWithValue("Id", organizerId);
            update.Parameters.AddWithValue("Status", decision.TargetStatus!);
            await update.ExecuteNonQueryAsync();
        }

        await using (var audit = new NpgsqlCommand(@"
            INSERT INTO organizer_status_audit (id, organizer_id, action, reason, actor_sub, occurred_at)
            VALUES (@Id, @OrganizerId, @Action, @Reason, @ActorSub, @OccurredAt)", connection, transaction))
        {
            audit.Parameters.AddWithValue("Id", Guid.CreateVersion7());
            audit.Parameters.AddWithValue("OrganizerId", organizerId);
            audit.Parameters.AddWithValue("Action", action == OrganizerStatusAction.Suspend ? "Suspended" : "Reinstated");
            audit.Parameters.AddWithValue("Reason", reason);
            audit.Parameters.AddWithValue("ActorSub", actorSub);
            audit.Parameters.AddWithValue("OccurredAt", occurredAt);
            await audit.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new OrganizerStatusChangeResult(OrganizerStatusChangeOutcome.Changed, decision.TargetStatus);
    }

    public async Task<List<OrganizerStatusAuditEntry>> GetOrganizerStatusHistoryAsync(Guid organizerId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, organizer_id, action, reason, actor_sub, occurred_at
            FROM organizer_status_audit
            WHERE organizer_id = @OrganizerId
            ORDER BY occurred_at DESC, id DESC;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrganizerId", organizerId);
        await using var reader = await command.ExecuteReaderAsync();

        var entries = new List<OrganizerStatusAuditEntry>();
        while (await reader.ReadAsync())
        {
            entries.Add(new OrganizerStatusAuditEntry(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return entries;
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

    public async Task DeleteUserAccountAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = "DELETE FROM user_accounts WHERE id = @Id;";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        await command.ExecuteNonQueryAsync();
    }
}
