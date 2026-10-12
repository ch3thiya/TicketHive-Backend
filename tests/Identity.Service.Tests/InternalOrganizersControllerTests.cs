using System;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public async Task GetOrganizerBySub_RejectedOrganizerApplicant_ReturnsNotFound()
    {
        // Arrange: rejection resets the applicant to the plain-customer shape
        // (Role stays "Customer", ApprovalStatus reset from "pending" back to
        // "approved"), so this is the exact account shape rejection leaves behind.
        var account = Account("Customer", "approved");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetOrganizerBySub_ApprovedOrganizer_ReportsApprovedStatus()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(Account("Organizer", "approved"));

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        var response = Assert.IsType<OrganizerLookupResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("approved", response.Status);
    }

    [Fact]
    public async Task GetOrganizerBySub_SuspendedOrganizer_ReportsSuspendedStatus()
    {
        // Arrange
        var account = Account("Organizer", "suspended");
        _mockRepo.Setup(r => r.GetUserAccountBySubAsync("sub-1")).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerBySub("sub-1");

        // Assert
        var response = Assert.IsType<OrganizerLookupResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new OrganizerLookupResponse(account.Id, "suspended"), response);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    public async Task GetOrganizerById_NeverApprovedOrganizer_ReturnsNotFound(string status)
    {
        // Arrange
        var account = Account("Organizer", status);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(account.Id)).ReturnsAsync(account);

        // Act
        var result = await _controller.GetOrganizerById(account.Id);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetOrganizerStatuses_KnownIds_ReturnsStatuses()
    {
        // Arrange
        var approved = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetOrganizerStatusesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [approved] = "approved", [suspended] = "suspended" });

        // Act
        var result = await _controller.GetOrganizerStatuses(new OrganizerStatusBatchRequest([approved, suspended, suspended]));

        // Assert
        var list = Assert.IsType<List<OrganizerLookupResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, list.Count);
        _mockRepo.Verify(r => r.GetOrganizerStatusesAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task GetOrganizerStatuses_EmptyOrOversizedBatch_ReturnsBadRequest()
    {
        // Arrange
        var oversized = Enumerable.Range(0, InternalOrganizersController.MaxBatchSize + 1).Select(_ => Guid.NewGuid()).ToList();

        // Act
        var empty = await _controller.GetOrganizerStatuses(new OrganizerStatusBatchRequest([]));
        var tooMany = await _controller.GetOrganizerStatuses(new OrganizerStatusBatchRequest(oversized));

        // Assert
        Assert.Equal(400, Assert.IsType<ObjectResult>(empty).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>(tooMany).StatusCode);
    }
}
