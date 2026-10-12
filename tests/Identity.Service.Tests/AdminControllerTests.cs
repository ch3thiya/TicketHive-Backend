using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Identity.Service.Clients;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;
using Identity.Service.Services;

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

        _controller = new AdminController(_mockRepo.Object, _mockScimClient.Object, new Mock<IOrganizerSuspensionService>().Object, _mockLogger.Object);
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
    public async Task ApproveRequest_RequestAlreadyDecided_ReturnsConflict()
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
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

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
    public async Task RejectRequest_ValidPendingRequest_ResetsAccountToPlainCustomerAndLeavesIdentityUntouched()
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
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Customer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "rejected")).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RejectRequest(requestId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // The account row is reset to the plain-customer shape (never deleted)...
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Customer", "approved"), Times.Once);
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(It.IsAny<Guid>(), It.Is<string>(role => role != "Customer"), It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "rejected"), Times.Once);

        // ...and the applicant's Asgardeo identity is never touched.
        _mockRepo.Verify(r => r.GetUserAccountByIdAsync(It.IsAny<Guid>()), Times.Never);
        _mockScimClient.Verify(s => s.DeleteUserAsync(It.IsAny<string>()), Times.Never);
        _mockScimClient.Verify(s => s.UpdateApprovalStatusAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
    public async Task RejectRequest_RequestAlreadyDecided_ReturnsConflict()
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
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockScimClient.Verify(s => s.DeleteUserAsync(It.IsAny<string>()), Times.Never);
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

    // --- No exception detail in unexpected-failure responses ---

    [Fact]
    public async Task GetPendingRequests_UnexpectedException_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        const string secretExceptionText = "connection string password=super-secret";
        _mockRepo.Setup(r => r.GetPendingOrganizerRequestsAsync()).ThrowsAsync(new Exception(secretExceptionText));

        // Act
        var result = await _controller.GetPendingRequests();

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
    }

    [Fact]
    public async Task GetApprovedOrganizers_UnexpectedException_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        const string secretExceptionText = "connection string password=super-secret";
        _mockRepo.Setup(r => r.GetApprovedOrganizersAsync()).ThrowsAsync(new Exception(secretExceptionText));

        // Act
        var result = await _controller.GetApprovedOrganizers();

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
    }

    [Fact]
    public async Task ApproveRequest_LookupThrowsBeforeReachingSafeApprovalSteps_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange - an exception outside the two guarded try blocks (e.g. the
        // initial request lookup) is caught by the outer catch-all.
        const string secretExceptionText = "connection string password=super-secret";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ThrowsAsync(new Exception(secretExceptionText));

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
    }

    [Fact]
    public async Task RejectRequest_LookupThrows_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        const string secretExceptionText = "connection string password=super-secret";
        var requestId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ThrowsAsync(new Exception(secretExceptionText));

        // Act
        var result = await _controller.RejectRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
    }

    // --- Safe approval: failure and retry behaviour ---

    private static (OrganizerRequest request, UserAccount account) MakePendingRequestAndAccount(
        Guid requestId, Guid userAccountId, string wso2Sub = "wso2-sub-1", string email = "organizer@domain.com")
    {
        var request = new OrganizerRequest
        {
            Id = requestId,
            UserAccountId = userAccountId,
            OrganizationName = "Test Org",
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };

        var account = new UserAccount
        {
            Id = userAccountId,
            Wso2Sub = wso2Sub,
            Email = email,
            FullName = "Pending Organizer",
            Role = "Customer",
            ApprovalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };

        return (request, account);
    }

    private void VerifyLoggedAtLevel(LogLevel level, Guid requestId, Guid accountId, Times times)
    {
        _mockLogger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(requestId.ToString()) &&
                    state.ToString()!.Contains(accountId.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }

    private static void AssertNoLocalWritesHappened(Mock<IAccountRepository> mockRepo)
    {
        mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ApproveRequest_AttributeUpdateFails_ReturnsServiceUnavailableAndWritesNothingLocally()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient
            .Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved"))
            .ThrowsAsync(new HttpRequestException("asgardeo-attribute-patch-unreachable"));

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        _mockScimClient.Verify(s => s.AssignUserToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        AssertNoLocalWritesHappened(_mockRepo);
    }

    [Fact]
    public async Task ApproveRequest_GroupAssignmentFails_ReturnsServiceUnavailableAndWritesNothingLocally()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient
            .Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer"))
            .ThrowsAsync(new HttpRequestException("asgardeo-group-patch-unreachable"));

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        AssertNoLocalWritesHappened(_mockRepo);
    }

    [Fact]
    public async Task ApproveRequest_FirstLocalWriteFails_ReturnsServiceUnavailableAndLogsWarningWithIds()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo
            .Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"))
            .ThrowsAsync(new Exception("db-unreachable"));

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        VerifyLoggedAtLevel(LogLevel.Warning, requestId, userAccountId, Times.Once());
    }

    [Fact]
    public async Task ApproveRequest_SecondLocalWriteFailsAfterFirstSucceeds_ReturnsServiceUnavailableAndLogsWarningWithIds()
    {
        // Arrange - the account write succeeds (account now reads Organizer/approved)
        // but the request write fails (request still reads pending). This is its
        // own case, distinct from the first-local-write failure above.
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo
            .Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"))
            .ThrowsAsync(new Exception("db-unreachable"));

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"), Times.Once);
        VerifyLoggedAtLevel(LogLevel.Warning, requestId, userAccountId, Times.Once());
    }

    [Theory]
    [InlineData("attribute-update")]
    [InlineData("group-assignment")]
    [InlineData("first-local-write")]
    [InlineData("second-local-write")]
    public async Task ApproveRequest_AnyFailurePoint_ServiceUnavailableResponseCarriesNoExceptionDetail(string failurePoint)
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);
        const string secretExceptionText = "connection string password=super-secret";

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).Returns(Task.CompletedTask);

        switch (failurePoint)
        {
            case "attribute-update":
                _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).ThrowsAsync(new Exception(secretExceptionText));
                break;
            case "group-assignment":
                _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).ThrowsAsync(new Exception(secretExceptionText));
                break;
            case "first-local-write":
                _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).ThrowsAsync(new Exception(secretExceptionText));
                break;
            case "second-local-write":
                _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).ThrowsAsync(new Exception(secretExceptionText));
                break;
        }

        // Act
        var result = await _controller.ApproveRequest(requestId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
    }

    [Fact]
    public async Task ApproveRequest_RetryAfterAttributeUpdateFailure_CompletesWithSameEndStateAsFirstTimeSuccess()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.SetupSequence(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved"))
            .ThrowsAsync(new HttpRequestException("asgardeo-attribute-patch-unreachable"))
            .Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).Returns(Task.CompletedTask);

        // Act - first attempt fails, retry (request is still "pending", so the guard lets it through) succeeds
        var firstAttempt = await _controller.ApproveRequest(requestId);
        var retryResult = await _controller.ApproveRequest(requestId);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(firstAttempt).StatusCode);
        Assert.IsType<OkObjectResult>(retryResult);

        _mockScimClient.Verify(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer"), Times.Once);
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"), Times.Once);
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"), Times.Once);
    }

    [Fact]
    public async Task ApproveRequest_RetryAfterGroupAssignmentFailure_ReassigningAnExistingMemberDoesNotFailApproval()
    {
        // Arrange - simulates the SCIM client's own idempotency: re-assigning a user
        // who is already a group member does not throw, it just succeeds again.
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.SetupSequence(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer"))
            .ThrowsAsync(new HttpRequestException("asgardeo-group-patch-unreachable"))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).Returns(Task.CompletedTask);

        // Act
        var firstAttempt = await _controller.ApproveRequest(requestId);
        var retryResult = await _controller.ApproveRequest(requestId);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(firstAttempt).StatusCode);
        Assert.IsType<OkObjectResult>(retryResult);

        _mockScimClient.Verify(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved"), Times.Exactly(2));
        _mockScimClient.Verify(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer"), Times.Exactly(2));
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"), Times.Once);
    }

    [Fact]
    public async Task ApproveRequest_RetryAfterFirstLocalWriteFailure_CompletesWithSameEndStateAsFirstTimeSuccess()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.SetupSequence(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"))
            .ThrowsAsync(new Exception("db-unreachable"))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved")).Returns(Task.CompletedTask);

        // Act
        var firstAttempt = await _controller.ApproveRequest(requestId);
        var retryResult = await _controller.ApproveRequest(requestId);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(firstAttempt).StatusCode);
        Assert.IsType<OkObjectResult>(retryResult);

        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"), Times.Exactly(2));
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"), Times.Once);
    }

    [Fact]
    public async Task ApproveRequest_RetryAfterSecondLocalWriteFailure_ReappliesFirstWriteHarmlesslyAndCompletes()
    {
        // Arrange - covers the case flagged during planning: the account write
        // already succeeded (account reads Organizer/approved) when the request
        // write failed (request still reads pending). Retry must redo the
        // already-applied account write harmlessly, then finish the request write.
        var requestId = Guid.NewGuid();
        var userAccountId = Guid.NewGuid();
        var (request, account) = MakePendingRequestAndAccount(requestId, userAccountId);

        _mockRepo.Setup(r => r.GetOrganizerRequestByIdAsync(requestId)).ReturnsAsync(request);
        _mockRepo.Setup(r => r.GetUserAccountByIdAsync(userAccountId)).ReturnsAsync(account);
        _mockScimClient.Setup(s => s.UpdateApprovalStatusAsync(account.Wso2Sub, "approved")).Returns(Task.CompletedTask);
        _mockScimClient.Setup(s => s.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer")).Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved")).Returns(Task.CompletedTask);
        _mockRepo.SetupSequence(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"))
            .ThrowsAsync(new Exception("db-unreachable"))
            .Returns(Task.CompletedTask);

        // Act - request.Status is never mutated by these mocks, so it is still
        // "pending" for the retry, exactly as it would be in the real database.
        var firstAttempt = await _controller.ApproveRequest(requestId);
        var retryResult = await _controller.ApproveRequest(requestId);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(firstAttempt).StatusCode);
        Assert.IsType<OkObjectResult>(retryResult);

        // The account write is safely re-applied with the same values on retry.
        _mockRepo.Verify(r => r.UpdateUserAccountRoleAndStatusAsync(userAccountId, "Organizer", "approved"), Times.Exactly(2));
        _mockRepo.Verify(r => r.UpdateOrganizerRequestStatusAsync(requestId, "approved"), Times.Exactly(2));
    }
}
