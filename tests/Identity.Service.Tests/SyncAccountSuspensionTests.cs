using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Identity.Service.Clients;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests;

// Logging in must never undo an admin's decision: a suspended organizer who signs in again
// stays suspended.
public class SyncAccountSuspensionTests
{
    private readonly Mock<IAccountRepository> _repository = new();
    private readonly Mock<IWso2ScimClient> _scim = new();

    private AuthController CreateController(string role)
    {
        var controller = new AuthController(_repository.Object, _scim.Object, NullLogger<AuthController>.Instance, new FakeTimeProvider())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "organizer-sub"),
                        new Claim("email", "organizer@example.com"),
                        new Claim("groups", role)
                    ], "test"))
                }
            }
        };
        return controller;
    }

    private UserAccount SuspendedOrganizer()
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Wso2Sub = "organizer-sub",
            Email = "organizer@example.com",
            FullName = "Olive",
            Role = "Organizer",
            ApprovalStatus = "suspended"
        };
        _repository.Setup(r => r.GetUserAccountBySubAsync("organizer-sub")).ReturnsAsync(account);
        _scim.Setup(s => s.UserExistsInAsgardeoAsync("organizer-sub")).ReturnsAsync(true);
        return account;
    }

    [Theory]
    [InlineData("Organizer")]
    [InlineData("Organizers")]
    [InlineData("Customer")]
    public async Task SyncAccount_SuspendedOrganizerSignsIn_StaysSuspendedAndNothingIsWritten(string tokenRole)
    {
        // Arrange
        var account = SuspendedOrganizer();
        var controller = CreateController(tokenRole);

        // Act
        var result = await controller.SyncAccount();

        // Assert
        var returned = Assert.IsType<UserAccount>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("suspended", returned.ApprovalStatus);
        Assert.Equal("Organizer", returned.Role);
        _repository.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(account.Id, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SyncAccount_SuspendedOrganizerRepeatedSignIns_NeverReinstate()
    {
        // Arrange
        SuspendedOrganizer();
        var controller = CreateController("Organizer");

        // Act
        for (var i = 0; i < 3; i++)
        {
            await controller.SyncAccount();
        }

        // Assert
        _repository.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}