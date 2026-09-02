using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Identity.Service.Clients;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests;

public class AdminControllerTests
{
    private readonly Mock<IAccountRepository> _mockRepo;
    private readonly Mock<IWso2ScimClient> _mockScimClient;
    private readonly Mock<ILogger<AdminController>> _mockLogger;
    private readonly AdminController _controller;

    public AdminControllerTests()
    {
        _mockRepo = new Mock<IAccountRepository>();
        _mockScimClient = new Mock<IWso2ScimClient>();
        _mockLogger = new Mock<ILogger<AdminController>>();

        _controller = new AdminController(_mockRepo.Object, _mockScimClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ApproveRequest_ValidPendingRequest_ApprovesWso2UserRoleAndLocalAccountAndReturnsOk()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var wso2Sub = "wso2-sub-approved-123";
        var userEmail = "organizer@domain.com";

        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            UserAccountId = userAccountId,
            OrganizationName = "Test Org",
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };

        var userAccount = new UserAccount
        {
            Id = userAccountId,
            Wso2Sub = wso2Sub,
            Email = userEmail,
            FullName = "Pending Organizer",
            Role = "Customer",
            ApprovalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(userAccount);

        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(wso2Sub, userEmail, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        _mockScimClient.Verify(s => s.UpdateApprovalStatusAsync(wso2Sub, "approved"), Times.Once);
        _mockScimClient.Verify(s => s.AssignUserToGroupAsync(wso2Sub, userEmail, "Organizer"), Times.Once);
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"), Times.Once);
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"), Times.Once);
    }

    [Fact]
    public async Task ApproveRequest_RequestNotFound_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(nonExistentId)).ReturnsAsync((OrganizerRequest?)null);

        // Act
        var result = await _controller.ApproveRequest(nonExistentId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task ApproveRequest_RequestAlreadyProcessed_ReturnsBadRequest()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            Status = "approved" // Already approved
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);

        _mockScimClient.Verify(s => s.UpdateApprovalStatusAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ApproveRequest_AssociatedUserAccountNotFound_ReturnsNotFound()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();

        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            UserAccountId = userAccountId,
            Status = "pending"
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync((UserAccount?)null);

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task RejectRequest_ValidPendingRequest_DeletesWso2UserAndLocalAccountAndReturnsOk()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var wso2Sub = "wso2-sub-rejected-456";

        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            UserAccountId = userAccountId,
            Status = "pending"
        };

        var userAccount = new UserAccount
        {
            Id = userAccountId,
            Wso2Sub = wso2Sub,
            Email = "rejected@domain.com",
            ApprovalStatus = "pending"
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(userAccount);

        _mockScimClient.Setup(s => s.DeleteUserAsync(wso2Sub)).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.DeleteUserAccountAsync(userAccountId)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RejectRequest(requestId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        _mockScimClient.Verify(s => s.DeleteUserAsync(wso2Sub), Times.Once);
        _mockRepo.Verify(r => r.DeleteUserAccountAsync(userAccountId), Times.Once);
    }

    [Fact]
    public async Task RejectRequest_RequestNotFound_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(nonExistentId)).ReturnsAsync((OrganizerRequest?)null);

        // Act
        var result = await _controller.RejectRequest(nonExistentId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task RejectRequest_RequestAlreadyProcessed_ReturnsBadRequest()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            Status = "rejected"
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);

        // Act
        var result = await _controller.RejectRequest(requestId);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);

        _mockScimClient.Verify(s => s.DeleteUserAsync(It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.DeleteUserAccountAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RejectRequest_AssociatedUserAccountNotFound_ReturnsNotFound()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();

        var organizerRequest = new OrganizerRequest
        {
            Id = requestId,
            UserAccountId = userAccountId,
            Status = "pending"
        };

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(organizerRequest);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync((UserAccount?)null);

        // Act
        var result = await _controller.RejectRequest(requestId);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task GetPendingRequests_ReturnsOkWithList()
    {
        // Arrange
        var pendingList = new List<Dictionary<string, object>>
        {
            new() { { "requestId", Guid.NewGuid() }, { "organizationName", "Org 1" }, { "status", "pending" } }
        };

        _mockRepo.Setup(r => r.GetPendingOrganizerRequestsAsync()).ReturnsAsync(pendingList);

        // Act
        var result = await _controller.GetPendingRequests();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(pendingList, okResult.Value);
    }

    [Fact]
    public async Task GetApprovedOrganizers_ReturnsOkWithList()
    {
        // Arrange
        var organizersList = new List<Dictionary<string, object>>
        {
            new() { { "accountId", Guid.NewGuid() }, { "email", "org@test.com" }, { "organizationName", "Approved Org" } }
        };

        _mockRepo.Setup(r => r.GetApprovedOrganizersAsync()).ReturnsAsync(organizersList);

        // Act
        var result = await _controller.GetApprovedOrganizers();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(organizersList, okResult.Value);
    }
}
