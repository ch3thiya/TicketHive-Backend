using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Services;

public class PayHereService : IPayHereService
{
    private readonly string _merchantId;
    private readonly string _merchantSecret;
    private readonly string _returnUrl;
    private readonly string _cancelUrl;
    private readonly string _notifyUrl;
    private readonly ILogger<PayHereService> _logger;

    public PayHereService(IConfiguration configuration, ILogger<PayHereService> logger)
    {
        _logger = logger;
        _merchantId = configuration["PayHere:MerchantId"] ?? "1238120";
        _merchantSecret = configuration["PayHere:MerchantSecret"] ?? "MTY2NTA5OTMzMTM4NjMzODUyMjgzMjYwMDEwMjkxMTgyNDc5MTcyNg==";
        _returnUrl = configuration["PayHere:ReturnUrl"] ?? "http://localhost:5173/checkout";
        _cancelUrl = configuration["PayHere:CancelUrl"] ?? "http://localhost:5173/checkout";
        _notifyUrl = configuration["PayHere:NotifyUrl"] ?? "http://localhost:5005/api/booking/payment/notify";
    }

    public PayHereCheckoutParams GenerateCheckoutParams(Guid orderId, decimal amount, string currency, string itemsSummary)
    {
        var orderIdStr = orderId.ToString();
        var formattedAmount = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var hash = GenerateHash(_merchantId, orderIdStr, formattedAmount, currency, _merchantSecret);

        return new PayHereCheckoutParams(
            MerchantId: _merchantId,
            OrderId: orderIdStr,
            Items: itemsSummary,
            Amount: amount,
            Currency: currency,
            Hash: hash,
            ReturnUrl: $"{_returnUrl}/{orderIdStr}",
            CancelUrl: $"{_cancelUrl}/{orderIdStr}",
            NotifyUrl: _notifyUrl
        );
    }

    public bool VerifyNotificationSignature(string merchantId, string orderId, string payhereAmount, string payhereCurrency, string statusCode, string md5sig)
    {
        if (string.IsNullOrWhiteSpace(md5sig)) return false;

        var secretMd5 = GetMd5Hash(_merchantSecret).ToUpperInvariant();
        var rawString = $"{merchantId}{orderId}{payhereAmount}{payhereCurrency}{statusCode}{secretMd5}";
        var calculatedSig = GetMd5Hash(rawString).ToUpperInvariant();

        var matches = string.Equals(calculatedSig, md5sig.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            _logger.LogWarning("PayHere signature mismatch for order {OrderId}. Calculated: {Calculated}, Received: {Received}", orderId, calculatedSig, md5sig);
        }

        return matches;
    }

    private static string GenerateHash(string merchantId, string orderId, string formattedAmount, string currency, string merchantSecret)
    {
        var secretMd5 = GetMd5Hash(merchantSecret).ToUpperInvariant();
        var rawString = $"{merchantId}{orderId}{formattedAmount}{currency}{secretMd5}";
        return GetMd5Hash(rawString).ToUpperInvariant();
    }

    private static string GetMd5Hash(string input)
    {
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes);
    }
}
