using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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

public class RegisterCustomerTests
{
    private readonly Mock<IAccountRepository> _mockRepo;
    private readonly Mock<IWso2ScimClient> _mockScimClient;
    private readonly Mock<ILogger<AuthController>> _mockLogger;
    private readonly AuthController _controller;

    public RegisterCustomerTests()
    {
        _mockRepo = new Mock<IAccountRepository>();
        _mockScimClient = new Mock<IWso2ScimClient>();
        _mockLogger = new Mock<ILogger<AuthController>>();

        _controller = new AuthController(_mockRepo.Object, _mockScimClient.Object, _mockLogger.Object, new FakeTimeProvider());
    }

    [Fact]
    public async Task RegisterCustomer_UnexpectedException_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        var request = new RegisterCustomerRequest(
            FullName: "Taylor Kim",
            Email: "taylor@customer.com",
            Password: "Password123!"
        );
        const string secretExceptionText = "connection string password=super-secret";

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync((UserAccount?)null);
        _mockScimClient.Setup(s => s.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                       .ThrowsAsync(new Exception(secretExceptionText));

        // Act
        var result = await _controller.RegisterCustomer(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Detail);
        Assert.DoesNotContain(secretExceptionText, problemDetails.Title);
        _mockRepo.Verify(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()), Times.Never);
    }

    [Fact]
    public async Task RegisterCustomer_ValidRequest_ReturnsOkAndCreatesApprovedAccount()
    {
        // Arrange
        var request = new RegisterCustomerRequest(
            FullName: "Taylor Kim",
            Email: "taylor@customer.com",
            Password: "Password123!"
        );
        const string wso2Id = "wso2-customer-id";

        _mockRepo.Setup(r => r.GetUserAccountByEmailAsync(request.Email)).ReturnsAsync((UserAccount?)null);
        _mockScimClient.Setup(s => s.CreateUserAsync(request.Email, request.Password, request.Email, request.FullName, "approved"))
                       .ReturnsAsync(wso2Id);

        UserAccount? savedAccount = null;
        _mockRepo.Setup(r => r.CreateUserAccountAsync(It.IsAny<UserAccount>()))
                 .Callback<UserAccount>(a => savedAccount = a)
                 .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RegisterCustomer(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(savedAccount);
        Assert.Equal(wso2Id, savedAccount.Wso2Sub);
        Assert.Equal("approved", savedAccount.ApprovalStatus);
    }
}
