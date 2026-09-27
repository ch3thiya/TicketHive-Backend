using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Payment.Service.Db;
using Payment.Service.Models;
using Payment.Service.Services;

namespace Payment.Service.Controllers;

public record CreateCheckoutRequest(Guid OrderId, decimal Amount, string Currency, string? ItemsSummary);

[ApiController]
[Route("api/payment")]
public class PaymentController : ControllerBase
{
    private readonly IPaymentRepository _repository;
    private readonly IPayHereService _payHereService;
    private readonly IKafkaPaymentProducer _kafkaProducer;
    private readonly TimeProvider _timeProvider;

    public PaymentController(
        IPaymentRepository repository,
        IPayHereService payHereService,
        IKafkaPaymentProducer kafkaProducer,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _payHereService = payHereService;
        _kafkaProducer = kafkaProducer;
        _timeProvider = timeProvider;
    }

    [HttpPost("checkout")]
    [Authorize]
    [ProducesResponseType(typeof(PayHereCheckoutParams), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateCheckoutRequest request)
    {
        var customerSub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(customerSub))
        {
            return Unauthorized();
        }

        var now = _timeProvider.GetUtcNow();
        var itemsSummary = request.ItemsSummary ?? $"TicketHive Order {request.OrderId}";

        var transaction = new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            OrderId = request.OrderId,
            CustomerSub = customerSub,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "LKR" : request.Currency,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.CreateAsync(transaction);

        var checkoutParams = _payHereService.GenerateCheckoutParams(
            request.OrderId,
            transaction.Amount,
            transaction.Currency,
            itemsSummary
        );

        return Ok(checkoutParams);
    }

    [HttpPost("notify")]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PaymentNotification(
        [FromForm] string? merchant_id,
        [FromForm] string? order_id,
        [FromForm] string? payment_id,
        [FromForm] string? payhere_amount,
        [FromForm] string? payhere_currency,
        [FromForm] string? status_code,
        [FromForm] string? md5sig)
    {
        merchant_id ??= Request.Form["merchant_id"].ToString();
        order_id ??= Request.Form["order_id"].ToString();
        payment_id ??= Request.Form["payment_id"].ToString();
        payhere_amount ??= Request.Form["payhere_amount"].ToString();
        payhere_currency ??= Request.Form["payhere_currency"].ToString();
        status_code ??= Request.Form["status_code"].ToString();
        md5sig ??= Request.Form["md5sig"].ToString();

        if (string.IsNullOrWhiteSpace(order_id) || string.IsNullOrWhiteSpace(md5sig))
        {
            return BadRequest("Missing required parameters");
        }

        var isValidSig = _payHereService.VerifyNotificationSignature(
            merchant_id ?? string.Empty,
            order_id,
            payhere_amount ?? string.Empty,
            payhere_currency ?? string.Empty,
            status_code ?? string.Empty,
            md5sig
        );

        if (!isValidSig)
        {
            return BadRequest("Invalid PayHere signature");
        }

        if (!Guid.TryParse(order_id, out var orderGuid))
        {
            return BadRequest("Invalid OrderId format");
        }

        var now = _timeProvider.GetUtcNow();

        if (status_code == "2") // 2 = Success in PayHere
        {
            await _repository.UpdateStatusAsync(orderGuid, PaymentStatus.Succeeded, payment_id, now);
            decimal.TryParse(payhere_amount, out var amount);

            await _kafkaProducer.PublishPaymentSucceededAsync(
                orderGuid,
                payment_id,
                amount,
                payhere_currency ?? "LKR",
                now
            );
        }
        else
        {
            await _repository.UpdateStatusAsync(orderGuid, PaymentStatus.Failed, payment_id, now);

            await _kafkaProducer.PublishPaymentFailedAsync(
                orderGuid,
                payment_id,
                $"PayHere status_code {status_code}",
                now
            );
        }

        return Ok();
    }

    [HttpPost("confirm-sandbox/{orderId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmSandboxPayment(Guid orderId)
    {
        var transaction = await _repository.GetByOrderIdAsync(orderId);
        var now = _timeProvider.GetUtcNow();
        var paymentId = $"SANDBOX_PAY_{Guid.NewGuid().ToString("N")[..8].ToUpper()}";

        var amount = transaction?.Amount ?? 0m;
        var currency = transaction?.Currency ?? "LKR";

        await _repository.UpdateStatusAsync(orderId, PaymentStatus.Succeeded, paymentId, now);
        await _kafkaProducer.PublishPaymentSucceededAsync(orderId, paymentId, amount, currency, now);

        return Ok(new { message = "Sandbox payment confirmed successfully.", orderId, paymentId });
    }
}

