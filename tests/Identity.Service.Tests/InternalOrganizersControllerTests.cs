using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests;

public class InternalOrganizersControllerTests
{
    private readonly Mock<IAccountRepository> _mockRepo;
    private readonly InternalOrganizersController _controller;

    public InternalOrganizersControllerTests()
    {
        _mockRepo = new Mock<IAccountRepository>();
        _controller = new InternalOrganizersController(_mockRepo.Object);
    }

    private static UserAccount Account(string role, string approvalStatus) => new()
    {
        Id = Guid.NewGuid(),
        Wso2Sub = "sub-1",
        Email = "user@example.com",
        FullName = "Test User",
        Role = role,
        ApprovalStatus = approvalStatus,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GetOrganizerBySub_ApprovedOrganizer_ReturnsOrganizerId()
    {
        // Arrange
        var account = Account("Organizer", "approved");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<OrganizerLookupResponse>(okResult.Value);
        Assert.Equal(account.Id, response.OrganizerId);
    }

    [Fact]
    public async Task GetOrganizerBySub_UnknownSubject_ReturnsNotFound()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("missing-sub")).ReturnsAsync((UserAccount?)null);

        // Act
        var result = await _controller.GetOrganizerBySub("missing-sub");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetOrganizerBySub_CustomerAccount_ReturnsNotFound()
    {
        // Arrange
        var account = Account("Customer", "approved");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetOrganizerBySub_PendingOrganizer_ReturnsNotFound()
    {
        // Arrange
        var account = Account("Organizer", "pending");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetOrganizerBySub_RejectedOrganizer_ReturnsNotFound()
    {
        // Arrange
        var account = Account("Organizer", "rejected");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }
}
