using Xunit;
using Catalog.Service.Models;

namespace Catalog.Service.Tests;

public class EventStatusTransitionsTests
{
    [Theory]
    [InlineData("Draft", "Published")]
    [InlineData("Draft", "Cancelled")]
    [InlineData("Published", "Cancelled")]
    public void CanTransition_AllowedTransition_ReturnsTrue(string from, string to)
    {
        // Act
        var result = EventStatusTransitions.CanTransition(from, to, out var reason);

        // Assert
        Assert.True(result);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("Cancelled", "Published")]
    [InlineData("Published", "Published")]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Draft", "Draft")]
    [InlineData("Published", "Draft")]
    [InlineData("Cancelled", "Draft")]
    public void CanTransition_RefusedTransition_ReturnsFalseWithReason(string from, string to)
    {
        // Act
        var result = EventStatusTransitions.CanTransition(from, to, out var reason);

        // Assert
        Assert.False(result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Published")]
    public void CanEdit_NotCancelled_ReturnsTrue(string status)
    {
        // Act
        var result = EventStatusTransitions.CanEdit(status, out var reason);

        // Assert
        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void CanEdit_Cancelled_ReturnsFalseWithReason()
    {
        // Act
        var result = EventStatusTransitions.CanEdit("Cancelled", out var reason);

        // Assert
        Assert.False(result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
