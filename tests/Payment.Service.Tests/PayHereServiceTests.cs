using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Payment.Service.Services;
using Xunit;

namespace Payment.Service.Tests;

public class PayHereServiceTests
{
    private readonly IPayHereService _payHereService;

    public PayHereServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["PayHere:MerchantId"] = "1238120",
                ["PayHere:MerchantSecret"] = "MjM1OTc0NjAzMjM5Njc3OTg3MTExNjY0NjgzODEyMTI0NDYxODM3Mw==",
                ["PayHere:ReturnUrl"] = "http://localhost:5173/checkout",
                ["PayHere:CancelUrl"] = "http://localhost:5173/checkout",
                ["PayHere:NotifyUrl"] = "http://localhost:5006/api/payment/notify"
            })
            .Build();

        var logger = new Mock<ILogger<PayHereService>>().Object;
        _payHereService = new PayHereService(config, logger);
    }

    [Fact]
    public void GenerateCheckoutParams_ReturnsValidParamsAndHash()
    {
        var orderId = Guid.NewGuid();
        var amount = 1500.00m;
        var currency = "LKR";

        var paramsResult = _payHereService.GenerateCheckoutParams(orderId, amount, currency, "Test Ticket");

        Assert.Equal("1238120", paramsResult.MerchantId);
        Assert.Equal(orderId.ToString(), paramsResult.OrderId);
        Assert.Equal(1500.00m, paramsResult.Amount);
        Assert.Equal("LKR", paramsResult.Currency);
        Assert.Equal("Test Ticket", paramsResult.Items);
        Assert.Equal($"http://localhost:5173/checkout/{orderId}", paramsResult.ReturnUrl);
        Assert.Equal($"http://localhost:5173/checkout/{orderId}", paramsResult.CancelUrl);
        Assert.Equal("http://localhost:5006/api/payment/notify", paramsResult.NotifyUrl);

        var secretHex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            "MjM1OTc0NjAzMjM5Njc3OTg3MTExNjY0NjgzODEyMTI0NDYxODM3Mw==")));
        var raw = $"1238120{orderId}1500.00LKR{secretHex}";
        Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))), paramsResult.Hash);
    }

    [Fact]
    public void VerifyNotificationSignature_ValidatesCorrectSignature()
    {
        var merchantId = "1238120";
        var orderId = Guid.NewGuid().ToString();
        var amount = "1500.00";
        var currency = "LKR";
        var statusCode = "2";

        var secretMd5 = MD5.HashData(Encoding.UTF8.GetBytes("MjM1OTc0NjAzMjM5Njc3OTg3MTExNjY0NjgzODEyMTI0NDYxODM3Mw=="));
        var secretHex = Convert.ToHexString(secretMd5).ToUpperInvariant();
        var raw = $"{merchantId}{orderId}{amount}{currency}{statusCode}{secretHex}";
        var expectedSig = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToUpperInvariant();

        var isValid = _payHereService.VerifyNotificationSignature(
            merchantId, orderId, amount, currency, statusCode, $"  {expectedSig.ToLowerInvariant()}  ");

        Assert.True(isValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-the-signature")]
    public void VerifyNotificationSignature_RejectsBlankOrIncorrectSignature(string signature)
    {
        Assert.False(_payHereService.VerifyNotificationSignature(
            "merchant", Guid.NewGuid().ToString(), "1500.00", "LKR", "2", signature));
    }

    [Fact]
    public void Constructor_RequiresMerchantId()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["PayHere:MerchantSecret"] = "secret"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => CreateService(configuration));

        Assert.Equal("PayHere:MerchantId configuration is missing.", exception.Message);
    }

    [Fact]
    public void Constructor_RequiresOneMerchantSecretSetting()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["PayHere:MerchantId"] = "merchant"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => CreateService(configuration));

        Assert.Equal("PayHere:MerchantSecret or PayHere:AppSecret configuration is missing.", exception.Message);
    }

    [Fact]
    public void Constructor_AcceptsLegacyAppSecretAndUsesDefaultUrls()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["PayHere:MerchantId"] = "merchant",
            ["PayHere:AppSecret"] = "legacy-secret"
        });
        var service = CreateService(configuration);
        var orderId = Guid.NewGuid();

        var result = service.GenerateCheckoutParams(orderId, 10m, "LKR", "Ticket");

        Assert.Equal($"http://localhost:5173/checkout/{orderId}", result.ReturnUrl);
        Assert.Equal($"http://localhost:5173/checkout/{orderId}", result.CancelUrl);
        Assert.Equal("http://localhost:5006/api/payment/notify", result.NotifyUrl);
        Assert.False(string.IsNullOrWhiteSpace(result.Hash));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static PayHereService CreateService(IConfiguration configuration) =>
        new(configuration, new Mock<ILogger<PayHereService>>().Object);
}
