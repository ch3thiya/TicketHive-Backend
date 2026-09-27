using System;

namespace Payment.Service.Services;

public record PayHereCheckoutParams(
    string MerchantId,
    string OrderId,
    string Items,
    decimal Amount,
    string Currency,
    string Hash,
    string ReturnUrl,
    string CancelUrl,
    string NotifyUrl
);

public interface IPayHereService
{
    PayHereCheckoutParams GenerateCheckoutParams(Guid orderId, decimal amount, string currency, string itemsSummary);
    bool VerifyNotificationSignature(string merchantId, string orderId, string payhereAmount, string payhereCurrency, string statusCode, string md5sig);
}
