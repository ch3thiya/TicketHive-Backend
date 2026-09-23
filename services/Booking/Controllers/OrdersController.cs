using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
[Route("api/booking/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly IPayHereService _payHereService;
    private readonly IOrderRepository _orderRepository;
    private readonly IInventoryClient _inventoryClient;
    private readonly ITicketService _ticketService;
    private readonly TimeProvider _timeProvider;

    public OrdersController(
        IOrderService orderService,
        IPayHereService payHereService,
        IOrderRepository orderRepository,
        IInventoryClient inventoryClient,
        ITicketService ticketService,
        TimeProvider timeProvider)
    {
        _orderService = orderService;
        _payHereService = payHereService;
        _orderRepository = orderRepository;
        _inventoryClient = inventoryClient;
        _ticketService = ticketService;
        _timeProvider = timeProvider;
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOrder(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(
                detail: "Header 'Idempotency-Key' is required.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing idempotency key");
        }

        var customerSub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(customerSub))
        {
            return Unauthorized();
        }

        var result = await _orderService.CreateOrderAsync(customerSub, idempotencyKey, request);

        return result.Status switch
        {
            CreateOrderStatus.Created => CreatedAtAction(nameof(GetOrder), new { id = result.Order!.OrderId }, result.Order),
            CreateOrderStatus.Duplicate => Ok(result.Order),
            CreateOrderStatus.HoldNotFound => Problem(detail: $"Hold '{request.HoldId}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Hold not found"),
            CreateOrderStatus.HoldUnauthorized => Problem(detail: "Hold does not belong to caller.", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden"),
            CreateOrderStatus.HoldNotActive => Problem(detail: "Hold is not active or has expired.", statusCode: StatusCodes.Status409Conflict, title: "Hold conflict"),
            CreateOrderStatus.FreezeFailed => Problem(detail: "Failed to lock hold for payment.", statusCode: StatusCodes.Status409Conflict, title: "Hold lock failed"),
            _ => Problem(detail: "An error occurred while creating order.", statusCode: StatusCodes.Status500InternalServerError, title: "Server error")
        };
    }

    [HttpGet("{id}")]
    [Authorize]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(Guid id)
    {
        var order = await _orderService.GetOrderAsync(id);
        if (order is null)
        {
            return Problem(detail: $"Order '{id}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Order not found");
        }

        return Ok(_orderService.ToResponse(order));
    }

    [HttpGet("{id}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderStatus(Guid id)
    {
        var order = await _orderService.GetOrderAsync(id);
        if (order is null)
        {
            return Problem(detail: $"Order '{id}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Order not found");
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = order.CreatedAt.AddMinutes(10);
        var isExpired = order.Status == OrderStatus.PaymentPending && now >= expiresAt;

        if (isExpired && order.Status == OrderStatus.PaymentPending)
        {
            await _orderRepository.UpdateStatusAsync(order.Id, OrderStatus.Failed, now);
            try { await _inventoryClient.ReleaseHoldAsync(order.HoldId); } catch { }
        }

        var currentStatus = isExpired ? OrderStatus.Failed : order.Status;

        return Ok(new
        {
            orderId = order.Id,
            status = currentStatus.ToString(),
            totalAmount = order.TotalAmount,
            currency = order.Currency,
            createdAt = order.CreatedAt,
            expiresAt = expiresAt,
            isExpired = isExpired,
            updatedAt = order.UpdatedAt
        });
    }

    [HttpGet("{id}/checkout")]
    [Authorize]
    [ProducesResponseType(typeof(PayHereCheckoutParams), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCheckoutParams(Guid id)
    {
        var order = await _orderService.GetOrderAsync(id);
        if (order is null)
        {
            return Problem(detail: $"Order '{id}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Order not found");
        }

        var itemsSummary = $"TicketHive Order {order.Id}";
        var checkoutParams = _payHereService.GenerateCheckoutParams(order.Id, order.TotalAmount, order.Currency, itemsSummary);

        return Ok(checkoutParams);
    }

    [HttpPost("{id}/confirm-sandbox")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmSandboxOrder(Guid id)
    {
        var order = await _orderService.GetOrderAsync(id);
        if (order is null)
        {
            return Problem(detail: $"Order '{id}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Order not found");
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = order.CreatedAt.AddMinutes(10);

        if (order.Status == OrderStatus.PaymentPending && now >= expiresAt)
        {
            await _orderRepository.UpdateStatusAsync(order.Id, OrderStatus.Failed, now);
            try { await _inventoryClient.ReleaseHoldAsync(order.HoldId); } catch { }
            return Problem(detail: "Hold has expired. Ticket is no longer available.", statusCode: StatusCodes.Status409Conflict, title: "Hold Expired");
        }

        if (order.Status != OrderStatus.Confirmed)
        {
            await _orderRepository.UpdateStatusAsync(order.Id, OrderStatus.Confirmed, now);
            try { await _inventoryClient.ConvertHoldAsync(order.HoldId); } catch { }
            await _ticketService.IssueTicketsForOrderAsync(order);
        }

        return Ok(new
        {
            orderId = order.Id,
            status = OrderStatus.Confirmed.ToString(),
            message = "Sandbox payment confirmed and tickets issued successfully."
        });
    }
}


