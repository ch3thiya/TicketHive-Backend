namespace Payment.Service.Models;

public record RefundRequest(decimal Amount, string Currency);
public record RefundResponse(Guid OrderId, decimal Amount, string Currency, string Status, string Reason);
