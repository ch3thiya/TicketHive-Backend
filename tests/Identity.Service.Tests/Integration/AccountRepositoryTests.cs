using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Service.Db;
using Identity.Service.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Identity.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class AccountRepositoryTests
{
    private readonly PostgresFixture _db;

    public AccountRepositoryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CreateUserAccountAsync_ThenGetBySub_ReturnsSameAccount()
    {
        // Arrange
        var repository = CreateRepository();
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Wso2Sub = $"sub-{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            FullName = "Test Organizer",
            Role = "Organizer",
            ApprovalStatus = "pending"
        };

        // Act
        await repository.CreateUserAccountAsync(account);
        var fetched = await repository.GetUserAccountBySubAsync(account.Wso2Sub);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(account.Id, fetched!.Id);
        Assert.Equal(account.Email, fetched.Email);
        Assert.Equal(account.FullName, fetched.FullName);
        Assert.Equal(account.Role, fetched.Role);
        Assert.Equal(account.ApprovalStatus, fetched.ApprovalStatus);
    }

    [Fact]
    public async Task CreateOrganizerRequestAsync_ForExistingAccount_LinksToAccount()
    {
        // Arrange
        var repository = CreateRepository();
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Wso2Sub = $"sub-{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            FullName = "Pending Organizer",
            Role = "Customer",
            ApprovalStatus = "pending"
        };
        await repository.CreateUserAccountAsync(account);

        var request = new OrganizerRequest
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            OrganizationName = "Neon Events",
            BusinessEmail = "org@example.com",
            Phone = "+94770000000",
            EventType = "Concert",
            About = "We run concerts."
        };

        // Act
        await repository.CreateOrganizerRequestAsync(request);
        var fetched = await repository.GetOrganizerRequestByIdAsync(request.Id);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(account.Id, fetched!.UserAccountId);
        Assert.Equal("Neon Events", fetched.OrganizationName);
        Assert.Equal("pending", fetched.Status);
    }

    private AccountRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new AccountRepository(new DbConnectionFactory(configuration));
    }
}
