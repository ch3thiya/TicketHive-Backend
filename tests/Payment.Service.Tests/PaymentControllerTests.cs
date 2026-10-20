using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Payment.Service.Controllers;
using Payment.Service.Db;
using Payment.Service.Models;
using Payment.Service.Services;
using Xunit;

namespace Payment.Service.Tests;

public class PaymentControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);

    private readonly Mock<IPaymentRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IPayHereService> _payHere = new(MockBehavior.Strict);
    private readonly Mock<IKafkaPaymentProducer> _producer = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task CreateCheckout_without_customer_identity_is_unauthorized_and_has_no_side_effects()
    {
        var controller = CreateController();

        var result = await controller.CreateCheckout(new CreateCheckoutRequest(Guid.NewGuid(), 250m, "LKR", null));

        Assert.IsType<UnauthorizedResult>(result);
        VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task CreateCheckout_persists_pending_transaction_and_returns_gateway_parameters(string claimType)
    {
        var orderId = Guid.NewGuid();
        PaymentTransaction? captured = null;
        var checkout = Checkout(orderId, 250m, "LKR", "Two tickets");
        _repository.Setup(r => r.CreateAsync(It.IsAny<PaymentTransaction>()))
            .Callback<PaymentTransaction>(value => captured = value)
            .ReturnsAsync((PaymentTransaction value) => value);
        _payHere.Setup(p => p.GenerateCheckoutParams(orderId, 250m, "LKR", "Two tickets"))
            .Returns(checkout);
        var controller = CreateController(new Claim(claimType, "customer-123"));

        var result = await controller.CreateCheckout(new CreateCheckoutRequest(orderId, 250m, "LKR", "Two tickets"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(checkout, ok.Value);
        Assert.NotNull(captured);
        Assert.NotEqual(Guid.Empty, captured.Id);
        Assert.Equal(orderId, captured.OrderId);
        Assert.Equal("customer-123", captured.CustomerSub);
        Assert.Equal(250m, captured.Amount);
        Assert.Equal("LKR", captured.Currency);
        Assert.Equal(PaymentStatus.Pending, captured.Status);
        Assert.Equal(Now, captured.CreatedAt);
        Assert.Equal(Now, captured.UpdatedAt);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateCheckout_defaults_blank_currency_and_missing_summary()
    {
        var orderId = Guid.NewGuid();
        _repository.Setup(r => r.CreateAsync(It.Is<PaymentTransaction>(t =>
                t.OrderId == orderId && t.Currency == "LKR")))
            .ReturnsAsync((PaymentTransaction value) => value);
        _payHere.Setup(p => p.GenerateCheckoutParams(orderId, 99.50m, "LKR", $"TicketHive Order {orderId}"))
            .Returns(Checkout(orderId, 99.50m, "LKR", $"TicketHive Order {orderId}"));
        var controller = CreateController(new Claim("sub", "customer-123"));

        var result = await controller.CreateCheckout(new CreateCheckoutRequest(orderId, 99.50m, "  ", null));

        Assert.IsType<OkObjectResult>(result);
        VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, "signature")]
    [InlineData("", "signature")]
    [InlineData("not-an-order", null)]
    [InlineData("not-an-order", "")]
    public async Task PaymentNotification_missing_required_fields_is_rejected(string? orderId, string? signature)
    {
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification("merchant", orderId, "payment", "250.00", "LKR", "2", signature);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Missing required parameters", badRequest.Value);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PaymentNotification_invalid_signature_is_rejected_without_side_effects()
    {
        var orderId = Guid.NewGuid().ToString();
        _payHere.Setup(p => p.VerifyNotificationSignature("merchant", orderId, "250.00", "LKR", "2", "bad"))
            .Returns(false);
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification("merchant", orderId, "payment", "250.00", "LKR", "2", "bad");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Invalid PayHere signature", badRequest.Value);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PaymentNotification_signed_non_guid_order_is_rejected_without_side_effects()
    {
        _payHere.Setup(p => p.VerifyNotificationSignature("merchant", "not-a-guid", "250.00", "LKR", "2", "valid"))
            .Returns(true);
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification("merchant", "not-a-guid", "payment", "250.00", "LKR", "2", "valid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Invalid OrderId format", badRequest.Value);
        VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, "250.00", "LKR")]
    [InlineData("250", "not-money", "LKR")]
    [InlineData("250", "251.00", "LKR")]
    [InlineData("250", "250.00", "USD")]
    public async Task Signed_success_with_unknown_or_mismatched_checkout_is_rejected(
        string? storedAmount,
        string notificationAmount,
        string notificationCurrency)
    {
        var orderId = Guid.NewGuid();
        SetupValidSignature(orderId, notificationAmount, notificationCurrency, "2");
        var transaction = storedAmount is null
            ? null
            : new PaymentTransaction
            {
                OrderId = orderId,
                Amount = decimal.Parse(storedAmount, CultureInfo.InvariantCulture),
                Currency = "LKR"
            };
        _repository.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(transaction);
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification(
            "merchant", orderId.ToString(), "payment", notificationAmount, notificationCurrency, "2", "valid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Verified payment amount or currency does not match checkout.", badRequest.Value);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Signed_success_updates_transaction_and_publishes_matching_event()
    {
        var orderId = Guid.NewGuid();
        SetupValidSignature(orderId, "250.00", "lkr", "2");
        _repository.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(new PaymentTransaction
        {
            OrderId = orderId,
            Amount = 250m,
            Currency = "LKR"
        });
        _repository.Setup(r => r.UpdateStatusAsync(orderId, PaymentStatus.Succeeded, "payment-1", Now))
            .ReturnsAsync(true);
        _producer.Setup(p => p.PublishPaymentSucceededAsync(orderId, "payment-1", 250m, "LKR", Now))
            .Returns(Task.CompletedTask);
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification(
            "merchant", orderId.ToString(), "payment-1", "250.00", "lkr", "2", "valid");

        Assert.IsType<OkResult>(result);
        VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3")]
    public async Task Signed_non_success_updates_failed_status_and_publishes_failure(string statusCode)
    {
        var orderId = Guid.NewGuid();
        SetupValidSignature(orderId, "250.00", "LKR", statusCode);
        _repository.Setup(r => r.UpdateStatusAsync(orderId, PaymentStatus.Failed, "payment-1", Now))
            .ReturnsAsync(true);
        _producer.Setup(p => p.PublishPaymentFailedAsync(
                orderId, "payment-1", $"PayHere status_code {statusCode}", Now))
            .Returns(Task.CompletedTask);
        var controller = CreateControllerWithEmptyForm();

        var result = await controller.PaymentNotification(
            "merchant", orderId.ToString(), "payment-1", "250.00", "LKR", statusCode, "valid");

        Assert.IsType<OkResult>(result);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PaymentNotification_reads_form_when_model_bound_values_are_null()
    {
        var orderId = Guid.NewGuid();
        SetupValidSignature(orderId, "250.00", "LKR", "0");
        _repository.Setup(r => r.UpdateStatusAsync(orderId, PaymentStatus.Failed, "payment-1", Now))
            .ReturnsAsync(true);
        _producer.Setup(p => p.PublishPaymentFailedAsync(orderId, "payment-1", "PayHere status_code 0", Now))
            .Returns(Task.CompletedTask);
        var controller = CreateController(form: new Dictionary<string, StringValues>
        {
            ["merchant_id"] = "merchant",
            ["order_id"] = orderId.ToString(),
            ["payment_id"] = "payment-1",
            ["payhere_amount"] = "250.00",
            ["payhere_currency"] = "LKR",
            ["status_code"] = "0",
            ["md5sig"] = "valid"
        });

        var result = await controller.PaymentNotification(null, null, null, null, null, null, null);

        Assert.IsType<OkResult>(result);
        VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmSandboxPayment_uses_stored_amount_currency_and_current_time()
    {
        var orderId = Guid.NewGuid();
        _repository.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(new PaymentTransaction
        {
            OrderId = orderId,
            Amount = 250m,
            Currency = "USD"
        });
        _repository.Setup(r => r.UpdateStatusAsync(
                orderId, PaymentStatus.Succeeded, It.Is<string>(id => id.StartsWith("SANDBOX_PAY_")), Now))
            .ReturnsAsync(true);
        _producer.Setup(p => p.PublishPaymentSucceededAsync(
                orderId, It.Is<string>(id => id.StartsWith("SANDBOX_PAY_")), 250m, "USD", Now))
            .Returns(Task.CompletedTask);
        var controller = CreateController();

        var result = await controller.ConfirmSandboxPayment(orderId);

        Assert.IsType<OkObjectResult>(result);
        _repository.VerifyAll();
        _producer.VerifyAll();
        _payHere.VerifyNoOtherCalls();
    }

    private void SetupValidSignature(Guid orderId, string amount, string currency, string statusCode)
    {
        _payHere.Setup(p => p.VerifyNotificationSignature(
                "merchant", orderId.ToString(), amount, currency, statusCode, "valid"))
            .Returns(true);
    }

    private PaymentController CreateController(
        Claim? claim = null,
        Dictionary<string, StringValues>? form = null)
    {
        var context = new DefaultHttpContext
        {
            User = claim is null
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity([claim], "test"))
        };
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(form ?? new Dictionary<string, StringValues>());

        return new PaymentController(_repository.Object, _payHere.Object, _producer.Object, _time)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private PaymentController CreateControllerWithEmptyForm() => CreateController();

    private static PayHereCheckoutParams Checkout(Guid orderId, decimal amount, string currency, string items) =>
        new("merchant", orderId.ToString(), items, amount, currency, "hash", "return", "cancel", "notify");

    private void VerifyNoOtherCalls()
    {
        _repository.VerifyAll();
        _payHere.VerifyAll();
        _producer.VerifyAll();
        _repository.VerifyNoOtherCalls();
        _payHere.VerifyNoOtherCalls();
        _producer.VerifyNoOtherCalls();
    }
}
