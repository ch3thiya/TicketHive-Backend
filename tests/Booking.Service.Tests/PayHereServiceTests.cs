using Booking.Service.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Booking.Service.Tests;

public class PayHereServiceTests
{
    private readonly IPayHereService _payHereService;

    public PayHereServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["PayHere:MerchantId"] = "1238120",
                ["PayHere:MerchantSecret"] = "MTY2NTA5OTMzMTM4NjMzODUyMjgzMjYwMDEwMjkxMTgyNDc5MTcyNg==",
                ["PayHere:ReturnUrl"] = "http://localhost:5173/checkout",
                ["PayHere:CancelUrl"] = "http://localhost:5173/checkout",
                ["PayHere:NotifyUrl"] = "http://localhost:5005/api/booking/payment/notify"
            })
            .Build();

        var logger = new Mock<ILogger<PayHereService>>().Object;
        _payHereService = new PayHereService(config, logger);
    }

    [Fact]
    public void GenerateCheckoutParams_ReturnsValidParamsAndHash()
    {
        var orderId = System.Guid.NewGuid();
        var amount = 1500.00m;
        var currency = "LKR";

        var paramsResult = _payHereService.GenerateCheckoutParams(orderId, amount, currency, "Test Ticket");

        Assert.Equal("1238120", paramsResult.MerchantId);
        Assert.Equal(orderId.ToString(), paramsResult.OrderId);
        Assert.Equal(1500.00m, paramsResult.Amount);
        Assert.Equal("LKR", paramsResult.Currency);
        Assert.False(string.IsNullOrWhiteSpace(paramsResult.Hash));
    }

    [Fact]
    public void VerifyNotificationSignature_ValidatesCorrectSignature()
    {
        var merchantId = "1238120";
        var orderId = System.Guid.NewGuid().ToString();
        var amount = "1500.00";
        var currency = "LKR";
        var statusCode = "2";

        var secretMd5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("MTY2NTA5OTMzMTM4NjMzODUyMjgzMjYwMDEwMjkxMTgyNDc5MTcyNg=="));
        var secretHex = System.Convert.ToHexString(secretMd5).ToUpperInvariant();
        var raw = $"{merchantId}{orderId}{amount}{currency}{statusCode}{secretHex}";
        var expectedSig = System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToUpperInvariant();

        var isValid = _payHereService.VerifyNotificationSignature(merchantId, orderId, amount, currency, statusCode, expectedSig);

        Assert.True(isValid);
    }
}
