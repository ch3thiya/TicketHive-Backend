using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class OrganizerSuspensionRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _db;

    public OrganizerSuspensionRepositoryTests(PostgresFixture db) => _db = db;

    private AccountRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
            .Build();
        return new AccountRepository(new DbConnectionFactory(configuration));
    }

    private async Task<UserAccount> SeedAsync(string role = "Organizer", string status = "approved")
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Wso2Sub = $"sub-{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            FullName = "Seeded",
            Role = role,
            ApprovalStatus = status
        };
        await CreateRepository().CreateUserAccountAsync(account);
        return account;
    }

    private async Task<string> StatusOfAsync(Guid id) =>
        (await CreateRepository().GetUserAccountByIdAsync(id))!.ApprovalStatus;

    [Fact]
    public async Task Suspend_ApprovedOrganizer_ChangesStatusAndWritesAuditRow()
    {
        // Arrange
        var organizer = await SeedAsync();
        var repository = CreateRepository();

        // Act
        var result = await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.Changed, result.Outcome);
        Assert.Equal("suspended", await StatusOfAsync(organizer.Id));
        var history = await repository.GetOrganizerStatusHistoryAsync(organizer.Id);
        var entry = Assert.Single(history);
        Assert.Equal(("Suspended", "Fraud", "admin-1"), (entry.Action, entry.Reason, entry.ActorSub));
        Assert.Equal(Now, entry.OccurredAt);
    }

    [Fact]
    public async Task Suspend_AlreadySuspended_IsUnchangedAndKeepsFirstAuditRow()
    {
        // Arrange
        var organizer = await SeedAsync();
        var repository = CreateRepository();
        await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "First", Now);

        // Act
        var result = await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-2", "Second", Now.AddMinutes(1));

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.Unchanged, result.Outcome);
        var entry = Assert.Single(await repository.GetOrganizerStatusHistoryAsync(organizer.Id));
        Assert.Equal("First", entry.Reason);
    }

    [Fact]
    public async Task Reinstate_SuspendedOrganizer_RestoresApprovedAndKeepsFullHistory()
    {
        // Arrange
        var organizer = await SeedAsync();
        var repository = CreateRepository();
        await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);

        // Act
        var result = await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Reinstate, "admin-2", "Resolved", Now.AddHours(1));

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.Changed, result.Outcome);
        Assert.Equal("approved", await StatusOfAsync(organizer.Id));
        var history = await repository.GetOrganizerStatusHistoryAsync(organizer.Id);
        Assert.Equal(new[] { "Reinstated", "Suspended" }, history.Select(h => h.Action).ToArray());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    public async Task Suspend_NeverApprovedOrganizer_IsInvalidTransitionWithoutAudit(string status)
    {
        // Arrange
        var organizer = await SeedAsync(status: status);
        var repository = CreateRepository();

        // Act
        var result = await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.InvalidTransition, result.Outcome);
        Assert.Equal(status, await StatusOfAsync(organizer.Id));
        Assert.Empty(await repository.GetOrganizerStatusHistoryAsync(organizer.Id));
    }

    [Fact]
    public async Task Suspend_CustomerAccountOrUnknownId_IsNotFound()
    {
        // Arrange
        var customer = await SeedAsync(role: "Customer");
        var repository = CreateRepository();

        // Act
        var customerResult = await repository.ApplyOrganizerStatusChangeAsync(customer.Id, OrganizerStatusAction.Suspend, "admin-1", "x", Now);
        var unknownResult = await repository.ApplyOrganizerStatusChangeAsync(Guid.NewGuid(), OrganizerStatusAction.Suspend, "admin-1", "x", Now);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.NotFound, customerResult.Outcome);
        Assert.Equal(OrganizerStatusChangeOutcome.NotFound, unknownResult.Outcome);
    }

    [Fact]
    public async Task ConcurrentSuspendAndReinstate_LeaveStatusMatchingLatestAuditEntry()
    {
        // Arrange
        var organizer = await SeedAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            await gate.Task;
            var action = i % 2 == 0 ? OrganizerStatusAction.Suspend : OrganizerStatusAction.Reinstate;
            return await CreateRepository().ApplyOrganizerStatusChangeAsync(organizer.Id, action, $"admin-{i}", $"reason-{i}", Now.AddSeconds(i));
        }).ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert
        var history = await CreateRepository().GetOrganizerStatusHistoryAsync(organizer.Id);
        Assert.Equal(results.Count(r => r.Outcome == OrganizerStatusChangeOutcome.Changed), history.Count);
        // Every recorded change was a real transition, so suspensions lead reinstatements by 0 or 1.
        var lead = history.Count(h => h.Action == "Suspended") - history.Count(h => h.Action == "Reinstated");
        Assert.InRange(lead, 0, 1);

        var expected = lead == 1 ? "suspended" : "approved";
        Assert.Equal(expected, await StatusOfAsync(organizer.Id));
    }

    [Fact]
    public async Task ConcurrentSuspend_SameOrganizer_WritesExactlyOneAuditRow()
    {
        // Arrange
        var organizer = await SeedAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            await gate.Task;
            return await CreateRepository().ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, $"admin-{i}", "Fraud", Now);
        }).ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, results.Count(r => r.Outcome == OrganizerStatusChangeOutcome.Changed));
        Assert.Equal(19, results.Count(r => r.Outcome == OrganizerStatusChangeOutcome.Unchanged));
        Assert.Single(await CreateRepository().GetOrganizerStatusHistoryAsync(organizer.Id));
    }

    [Fact]
    public async Task AuditTable_UpdateOrDelete_IsRejected()
    {
        // Arrange
        var organizer = await SeedAsync();
        await CreateRepository().ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        // Act
        await using var update = new NpgsqlCommand("UPDATE organizer_status_audit SET reason = 'edited' WHERE organizer_id = @Id", connection);
        update.Parameters.AddWithValue("Id", organizer.Id);
        await using var delete = new NpgsqlCommand("DELETE FROM organizer_status_audit WHERE organizer_id = @Id", connection);
        delete.Parameters.AddWithValue("Id", organizer.Id);

        // Assert
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task DeleteUserAccount_WithAuditHistory_SucceedsAndHistorySurvives()
    {
        // Arrange
        var organizer = await SeedAsync();
        var repository = CreateRepository();
        await repository.ApplyOrganizerStatusChangeAsync(organizer.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);

        // Act
        await repository.DeleteUserAccountAsync(organizer.Id);

        // Assert
        Assert.Null(await repository.GetUserAccountByIdAsync(organizer.Id));
        Assert.Single(await repository.GetOrganizerStatusHistoryAsync(organizer.Id));
    }

    [Fact]
    public async Task GetApprovedOrganizers_IncludesSuspendedWithLatestReasonAndTime()
    {
        // Arrange
        var suspended = await SeedAsync();
        var active = await SeedAsync();
        var repository = CreateRepository();
        await repository.ApplyOrganizerStatusChangeAsync(suspended.Id, OrganizerStatusAction.Suspend, "admin-1", "Old reason", Now);
        await repository.ApplyOrganizerStatusChangeAsync(suspended.Id, OrganizerStatusAction.Reinstate, "admin-1", "ok", Now.AddMinutes(1));
        await repository.ApplyOrganizerStatusChangeAsync(suspended.Id, OrganizerStatusAction.Suspend, "admin-2", "Latest reason", Now.AddMinutes(2));

        // Act
        var list = await repository.GetApprovedOrganizersAsync();

        // Assert
        var suspendedRow = list.Single(o => (Guid)o["accountId"] == suspended.Id);
        Assert.Equal("suspended", suspendedRow["status"]);
        Assert.Equal("Latest reason", suspendedRow["suspensionReason"]);
        Assert.Equal(Now.AddMinutes(2), (DateTimeOffset)suspendedRow["suspendedAt"]);
        var activeRow = list.Single(o => (Guid)o["accountId"] == active.Id);
        Assert.Equal("approved", activeRow["status"]);
        Assert.Null(activeRow["suspensionReason"]);
    }

    [Fact]
    public async Task GetOrganizerStatuses_ReturnsOnlyApprovedAndSuspendedOrganizers()
    {
        // Arrange
        var approved = await SeedAsync();
        var suspended = await SeedAsync();
        var pending = await SeedAsync(status: "pending");
        var customer = await SeedAsync(role: "Customer");
        var repository = CreateRepository();
        await repository.ApplyOrganizerStatusChangeAsync(suspended.Id, OrganizerStatusAction.Suspend, "admin-1", "Fraud", Now);

        // Act
        var statuses = await repository.GetOrganizerStatusesAsync([approved.Id, suspended.Id, pending.Id, customer.Id, Guid.NewGuid()]);

        // Assert
        Assert.Equal(2, statuses.Count);
        Assert.Equal("approved", statuses[approved.Id]);
        Assert.Equal("suspended", statuses[suspended.Id]);
    }
}