using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Payment.Service.Controllers;
using Payment.Service.Db;
using Payment.Service.Models;
using Payment.Service.Services;
using Xunit;

namespace Payment.Service.Tests;

public class PaymentControllerTests
{
    [Fact]
    public async Task Signed_success_with_different_amount_is_rejected_and_not_published()
    {
        var orderId = Guid.NewGuid();
        var repository = new Mock<IPaymentRepository>();
        repository.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(new PaymentTransaction { OrderId = orderId, Amount = 250, Currency = "LKR" });
        var payHere = new Mock<IPayHereService>();
        payHere.Setup(p => p.VerifyNotificationSignature(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var producer = new Mock<IKafkaPaymentProducer>();
        var controller = new PaymentController(repository.Object, payHere.Object, producer.Object, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.ContentType = "application/x-www-form-urlencoded";
        controller.HttpContext.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());

        var result = await controller.PaymentNotification("merchant", orderId.ToString(), "payment", "251.00", "LKR", "2", "signature");

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(r => r.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<PaymentStatus>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        producer.Verify(p => p.PublishPaymentSucceededAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }
}
