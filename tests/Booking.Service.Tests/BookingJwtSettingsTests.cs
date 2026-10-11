using Booking.Service.Authorization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Booking.Service.Tests;

public class BookingJwtSettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
        .Build();

    private static readonly (string, string?) Authority = ("Jwt:Authority", "https://issuer.test/token");

    [Fact]
    public void Load_ReadsArrayAndLegacySingleAudience()
    {
        var settings = BookingJwtSettings.Load(Config(Authority, ("Jwt:Audience", "spa"), ("Jwt:UserAudiences:0", "web"), ("Jwt:InternalAudiences:0", "m2m-a"), ("Jwt:InternalAudiences:1", "m2m-b")));

        Assert.Equal(new[] { "web", "spa" }, settings.UserAudiences);
        Assert.Equal(new[] { "m2m-a", "m2m-b" }, settings.InternalAudiences);
    }

    [Fact]
    public void Load_AcceptsCommaSeparatedEnvironmentStyleValues()
    {
        var settings = BookingJwtSettings.Load(Config(Authority, ("Jwt:Audience", "spa"), ("Jwt:InternalAudiences", "m2m-a, m2m-b")));

        Assert.Equal(new[] { "m2m-a", "m2m-b" }, settings.InternalAudiences);
    }

    [Fact]
    public void Load_MissingInternalAudiences_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BookingJwtSettings.Load(Config(Authority, ("Jwt:Audience", "spa"))));
        Assert.Contains("Jwt:InternalAudiences", ex.Message);
    }

    [Fact]
    public void Load_MissingUserAudiences_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BookingJwtSettings.Load(Config(Authority, ("Jwt:InternalAudiences:0", "m2m"))));
        Assert.Contains("Jwt:UserAudiences", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Load_BadAuthority_Fails(string? authority)
    {
        Assert.Throws<InvalidOperationException>(() =>
            BookingJwtSettings.Load(Config(("Jwt:Authority", authority), ("Jwt:Audience", "spa"), ("Jwt:InternalAudiences:0", "m2m"))));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("tickethive-*")]
    [InlineData("two words")]
    public void Load_WildcardOrWhitespaceAudience_Fails(string audience)
    {
        Assert.Throws<InvalidOperationException>(() =>
            BookingJwtSettings.Load(Config(Authority, ("Jwt:Audience", "spa"), ("Jwt:InternalAudiences:0", audience))));
    }

    [Fact]
    public void Load_AudienceSharedByBothSchemes_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BookingJwtSettings.Load(Config(Authority, ("Jwt:Audience", "same"), ("Jwt:InternalAudiences:0", "same"))));
        Assert.Contains("must not share", ex.Message);
    }
}
