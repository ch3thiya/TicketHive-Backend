using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Identity.Service.Clients;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests;

public class RegisterOrganizerTests
{
    private readonly Mock<IAccountRepository> _mockRepo;
    private readonly Mock<IWso2ScimClient> _mockScimClient;
    private readonly Mock<ILogger<AuthController>> _mockLogger;
    private readonly AuthController _controller;

    public RegisterOrganizerTests()
    {
        _mockRepo = new Mock<IAccountRepository>();
        _mockScimClient = new Mock<IWso2ScimClient>();
        _mockLogger = new Mock<ILogger<AuthController>>();

        _controller = new AuthController(_mockRepo.Object, _mockScimClient.Object, _mockLogger.Object, new FakeTimeProvider());
    }

    [Fact]
    public async Task RegisterOrganizer_ValidRequest_ReturnsOkAndCreatesRecords()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "Jane Doe",
            Email: "jane@org.com",
            Password: "SecurePassword123!",
            OrganizationName: "Tech Events Inc",
            BusinessEmail: "contact@techevents.com",
            Phone: "+1234567890",
            EventType: "Conference",
            About: "Annual tech conference organizer"
        );

        string generatedWso2Id = "wso2-user-guid-123";

        _mockRepo
            .Setup(r => r.GetUserAccountByEmailAsync(request.Email))
            .ReturnsAsync((UserAccount?)null);

        _mockScimClient
            .Setup(s => s.CreateUserAsync(request.Email, request.Password, request.Email, request.FullName, "pending"))
            .ReturnsAsync(generatedWso2Id);

        UserAccount? savedUserAccount = null;
        _mockRepo
            .Setup(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()))
            .Callback<UserAccount>(u => savedUserAccount = u)
            .Returns(Task.CompletedTask);

        OrganizerRequest? savedOrganizerRequest = null;
        _mockRepo
            .Setup(r => r.CreateOrganizerRequestAsync(It.IsAny<OrganizerRequest>()))
            .Callback<OrganizerRequest>(r => savedOrganizerRequest = r)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RegisterOrganizer(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        _mockScimClient.Verify(s => s.CreateUserAsync(request.Email, request.Password, request.Email, request.FullName, "pending"), Times.Once);
        _mockRepo.Verify(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()), Times.Once);
        _mockRepo.Verify(r => r.CreateOrganizerRequestAsync(It.IsAny<OrganizerRequest>()), Times.Once);

        Assert.NotNull(savedUserAccount);
        Assert.Equal(generatedWso2Id, savedUserAccount.Wso2Sub);
        Assert.Equal("jane@org.com", savedUserAccount.Email);
        Assert.Equal("Jane Doe", savedUserAccount.FullName);
        Assert.Equal("Customer", savedUserAccount.Role);
        Assert.Equal("pending", savedUserAccount.ApprovalStatus);

        Assert.NotNull(savedOrganizerRequest);
        Assert.Equal(savedUserAccount.Id, savedOrganizerRequest.UserAccountId);
        Assert.Equal("Tech Events Inc", savedOrganizerRequest.OrganizationName);
        Assert.Equal("contact@techevents.com", savedOrganizerRequest.BusinessEmail);
        Assert.Equal("+1234567890", savedOrganizerRequest.Phone);
        Assert.Equal("Conference", savedOrganizerRequest.EventType);
        Assert.Equal("Annual tech conference organizer", savedOrganizerRequest.About);
        Assert.Equal("pending", savedOrganizerRequest.Status);
    }

    [Fact]
    public async Task RegisterOrganizer_NewlyCreatedRequest_HasPendingStatus()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "Alex Smith",
            Email: "alex@events.com",
            Password: "Password123!",
            OrganizationName: "Alex Events",
            BusinessEmail: "biz@alexevents.com",
            Phone: "555-0199",
            EventType: "Music",
            About: "Music festival organizer"
        );

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync((UserAccount?)null);
        _mockScimClient.Setup(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "pending"))
                       .ReturnsAsync("wso2-id-456");

        UserAccount? createdAccount = null;
        OrganizerRequest? createdRequest = null;

        _mockRepo.Setup(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()))
                 .Callback<UserAccount>(a => createdAccount = a)
                 .Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.CreateOrganizerRequestAsync(It.IsAny<OrganizerRequest>()))
                 .Callback<OrganizerRequest>(req => createdRequest = req)
                 .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RegisterOrganizer(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(createdAccount);
        Assert.Equal("pending", createdAccount.ApprovalStatus);
        Assert.NotNull(createdRequest);
        Assert.Equal("pending", createdRequest.Status);
    }

    [Fact]
    public async Task RegisterOrganizer_AssociatesOrganizerRequestWithApplicationUser()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "Sam Wilson",
            Email: "sam@wilson.org",
            Password: "Pass12345Word!",
            OrganizationName: "Wilson Foundation",
            BusinessEmail: "info@wilson.org",
            Phone: "555-0188",
            EventType: "Charity",
            About: "Charity gala event coordinator"
        );

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync((UserAccount?)null);
        _mockScimClient.Setup(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                       .ReturnsAsync("wso2-sam-id");

        UserAccount? savedUser = null;
        OrganizerRequest? savedRequest = null;

        _mockRepo.Setup(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()))
                 .Callback<UserAccount>(u => savedUser = u)
                 .Returns(Task.CompletedTask);

        _mockRepo.Setup(r => r.CreateOrganizerRequestAsync(It.IsAny<OrganizerRequest>()))
                 .Callback<OrganizerRequest>(r => savedRequest = r)
                 .Returns(Task.CompletedTask);

        // Act
        await _controller.RegisterOrganizer(request);

        // Assert
        Assert.NotNull(savedUser);
        Assert.NotNull(savedRequest);
        Assert.NotEqual(Guid.Empty, savedUser.Id);
        Assert.Equal(savedUser.Id, savedRequest.UserAccountId);
    }

    [Fact]
    public async Task RegisterOrganizer_InvalidModelState_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "",
            Email: "invalid-email",
            Password: "",
            OrganizationName: "",
            BusinessEmail: "",
            Phone: "",
            EventType: "",
            About: ""
        );

        _controller.ModelState.AddModelError("Email", "Email is invalid");

        // Act
        var result = await _controller.RegisterOrganizer(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockScimClient.Verify(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()), Times.Never);
    }

    [Fact]
    public async Task RegisterOrganizer_DuplicateLocalEmail_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "Existing User",
            Email: "existing@tickethive.com",
            Password: "Password123!",
            OrganizationName: "Existing Org",
            BusinessEmail: "existing@tickethive.com",
            Phone: "555-1111",
            EventType: "Sports",
            About: "Sports organizer"
        );

        var existingUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = "existing@tickethive.com",
            FullName = "Existing User",
            Wso2Sub = "existing-wso2-id"
        };

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync(existingUser);

        // Act
        var result = await _controller.RegisterOrganizer(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockScimClient.Verify(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockRepo.Verify(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()), Times.Never);
    }

    [Fact]
    public async Task RegisterOrganizer_Wso2UserCreationFailsWithDuplicate_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterOrganizerRequest(
            FullName: "Wso2 Existing",
            Email: "wso2existing@tickethive.com",
            Password: "Password123!",
            OrganizationName: "Wso2 Org",
            BusinessEmail: "wso2existing@tickethive.com",
            Phone: "555-2222",
            EventType: "Expo",
            About: "Expo organizer"
        );

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync((UserAccount?)null);
        _mockScimClient.Setup(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                       .ThrowsAsync(new Exception("Failed to create user in identity provider: {\"detail\":\"User already exists in the system.\"}"));

        // Act
        var result = await _controller.RegisterOrganizer(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockRepo.Verify(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()), Times.Never);
    }
}
