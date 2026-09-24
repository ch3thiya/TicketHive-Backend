using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests;

public class NotificationServiceTests
{
    private readonly Mock<ILogger<EmailJsService>> _loggerMock = new();

    [Fact]
    public async Task SendTicketConfirmationAsync_SimulationMode_ReturnsSuccess_WhenCredentialsEmpty()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["EmailJS:ServiceId"] = "",
            ["EmailJS:PublicKey"] = ""
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var handlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(handlerMock.Object);
        var emailService = new EmailJsService(httpClient, config, _loggerMock.Object);

        var orderId = Guid.NewGuid();
        var ticketCodes = new List<string> { "TKT-TEST-0001" };

        // Act
        var result = await emailService.SendTicketConfirmationAsync(
            "customer@example.com",
            "John Doe",
            orderId,
            150.00m,
            "LKR",
            ticketCodes
        );

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SendTicketConfirmationAsync_ValidKeys_CallsHttpAndReturnsSuccess()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["EmailJS:ApiUrl"] = "https://api.emailjs.com/api/v1.0/email/send",
            ["EmailJS:ServiceId"] = "service_test123",
            ["EmailJS:TemplateId"] = "template_test123",
            ["EmailJS:PublicKey"] = "public_test123"
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("OK")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var emailService = new EmailJsService(httpClient, config, _loggerMock.Object);

        var orderId = Guid.NewGuid();
        var ticketCodes = new List<string> { "TKT-ABCD-EFGH-1234" };

        // Act
        var result = await emailService.SendTicketConfirmationAsync(
            "customer@tickethive.lk",
            "Chethiya",
            orderId,
            250.00m,
            "LKR",
            ticketCodes
        );

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri!.ToString().Contains("emailjs.com")),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task SendTicketConfirmationAsync_EmptyEmail_ReturnsFailed()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();
        var httpClient = new HttpClient(new Mock<HttpMessageHandler>().Object);
        var emailService = new EmailJsService(httpClient, config, _loggerMock.Object);

        // Act
        var result = await emailService.SendTicketConfirmationAsync(
            "",
            "Customer",
            Guid.NewGuid(),
            100m,
            "LKR",
            new List<string>()
        );

        // Assert
        Assert.False(result.Success);
        Assert.Contains("required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
