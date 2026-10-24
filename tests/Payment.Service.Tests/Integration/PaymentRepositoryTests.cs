using Microsoft.Extensions.Configuration;
using Payment.Service.Db;
using Payment.Service.Models;
using Xunit;

namespace Payment.Service.Tests.Integration;

[Collection("Postgres")]
public class PaymentRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Payment_round_trip_and_status_update_preserve_transaction_details()
    {
        var repository = CreateRepository();
        var orderId = Guid.CreateVersion7();
        Assert.Null(await repository.GetByOrderIdAsync(orderId));
        Assert.False(await repository.UpdateStatusAsync(orderId, PaymentStatus.Succeeded, null, DateTimeOffset.UtcNow));

        var createdAt = DateTimeOffset.UtcNow;
        var payment = new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            CustomerSub = "customer-123",
            Amount = 275.50m,
            Currency = "LKR",
            Status = PaymentStatus.Pending,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        Assert.Same(payment, await repository.CreateAsync(payment));
        var stored = await repository.GetByOrderIdAsync(orderId);
        Assert.NotNull(stored);
        Assert.Equal(payment.Id, stored.Id);
        Assert.Equal("customer-123", stored.CustomerSub);
        Assert.Equal(275.50m, stored.Amount);
        Assert.Equal("LKR", stored.Currency);
        Assert.Equal(PaymentStatus.Pending, stored.Status);
        Assert.Null(stored.PayHerePaymentId);

        var updatedAt = createdAt.AddMinutes(1);
        Assert.True(await repository.UpdateStatusAsync(orderId, PaymentStatus.Succeeded, "payhere-456", updatedAt));
        stored = await repository.GetByOrderIdAsync(orderId);
        Assert.NotNull(stored);
        Assert.Equal(PaymentStatus.Succeeded, stored.Status);
        Assert.Equal("payhere-456", stored.PayHerePaymentId);
        Assert.Equal(updatedAt.ToUnixTimeSeconds(), stored.UpdatedAt.ToUnixTimeSeconds());

        Assert.True(await repository.UpdateStatusAsync(orderId, PaymentStatus.Failed, null, updatedAt.AddMinutes(1)));
        Assert.Equal("payhere-456", (await repository.GetByOrderIdAsync(orderId))!.PayHerePaymentId);
    }

    private PaymentRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString
            })
            .Build();
        return new PaymentRepository(new DbConnectionFactory(configuration));
    }
}
