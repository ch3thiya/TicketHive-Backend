using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Notification.Service.Controllers;
using Notification.Service.Db;
using Notification.Service.Models;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests;

public class NotificationControllerTests
{
    [Fact]
    public async Task GetOrderNotifications_returns_repository_records()
    {
        var orderId = Guid.CreateVersion7();
        IReadOnlyList<NotificationRecord> records =
            [new NotificationRecord { OrderId = orderId, CustomerEmail = "customer@example.com" }];
        var repository = new Mock<INotificationRepository>();
        repository.Setup(x => x.GetByOrderIdAsync(orderId)).ReturnsAsync(records);
        var controller = new NotificationController(repository.Object, Mock.Of<IEmailService>(), TimeProvider.System);

        var response = await controller.GetOrderNotifications(orderId);

        Assert.Same(records, Assert.IsType<OkObjectResult>(response).Value);
        repository.Verify(x => x.GetByOrderIdAsync(orderId), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SendTestNotification_rejects_blank_email_without_sending(string email)
    {
        var repository = new Mock<INotificationRepository>();
        var sender = new Mock<IEmailService>();
        var controller = new NotificationController(repository.Object, sender.Object, TimeProvider.System);

        var response = await controller.SendTestNotification(new SendTestEmailRequest(email, "Customer", 10m, null));

        Assert.IsType<BadRequestObjectResult>(response);
        sender.VerifyNoOtherCalls();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendTestNotification_records_success_with_requested_ticket_details()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var repository = new Mock<INotificationRepository>();
        NotificationRecord? saved = null;
        repository.Setup(x => x.CreateAsync(It.IsAny<NotificationRecord>()))
            .Callback<NotificationRecord>(record => saved = record)
            .ReturnsAsync((NotificationRecord record) => record);
        var sender = new Mock<IEmailService>();
        sender.Setup(x => x.SendTicketConfirmationAsync(
                "customer@example.com", "Customer", It.IsAny<Guid>(), 125m, "LKR",
                It.Is<IReadOnlyList<string>>(codes => codes.Count == 1 && codes[0] == "TKT-123")))
            .ReturnsAsync(new EmailResult(true, null));
        var controller = new NotificationController(repository.Object, sender.Object, clock);

        var response = await controller.SendTestNotification(
            new SendTestEmailRequest("customer@example.com", "Customer", 125m, ["TKT-123"]));

        Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(saved);
        Assert.Equal("Sent", saved.Status);
        Assert.Equal("customer@example.com", saved.CustomerEmail);
        Assert.Equal(now, saved.SentAt);
        Assert.NotEqual(Guid.Empty, saved.OrderId);
        sender.VerifyAll();
        repository.VerifyAll();
    }

    [Fact]
    public async Task SendTestNotification_records_failed_delivery_with_defaults()
    {
        var repository = new Mock<INotificationRepository>();
        NotificationRecord? saved = null;
        repository.Setup(x => x.CreateAsync(It.IsAny<NotificationRecord>()))
            .Callback<NotificationRecord>(record => saved = record)
            .ReturnsAsync((NotificationRecord record) => record);
        var sender = new Mock<IEmailService>();
        sender.Setup(x => x.SendTicketConfirmationAsync(
                "customer@example.com", "Customer", It.IsAny<Guid>(), 50m, "LKR",
                It.Is<IReadOnlyList<string>>(codes => codes.Count == 1 && codes[0] == "TKT-TEST-1234-5678")))
            .ReturnsAsync(new EmailResult(false, "provider unavailable"));
        var controller = new NotificationController(repository.Object, sender.Object, TimeProvider.System);

        var response = await controller.SendTestNotification(
            new SendTestEmailRequest("customer@example.com", "Customer", 0m, null));

        Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(saved);
        Assert.Equal("Failed", saved.Status);
        Assert.Equal("provider unavailable", saved.ErrorMessage);
        sender.VerifyAll();
        repository.VerifyAll();
    }
}
