using Xunit;
using Catalog.Service.Models;

namespace Catalog.Service.Tests;

public class ShowStatusTransitionsTests
{
    [Fact]
    public void CanTransition_ActiveToCancelled_ReturnsTrue()
    {
        // Act
        var result = ShowStatusTransitions.CanTransition("Active", "Cancelled", out var reason);

        // Assert
        Assert.True(result);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Cancelled", "Active")]
    [InlineData("Active", "Active")]
    public void CanTransition_RefusedTransition_ReturnsFalseWithReason(string from, string to)
    {
        // Act
        var result = ShowStatusTransitions.CanTransition(from, to, out var reason);

        // Assert
        Assert.False(result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void CanEdit_Active_ReturnsTrue()
    {
        // Act
        var result = ShowStatusTransitions.CanEdit("Active", out var reason);

        // Assert
        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void CanEdit_Cancelled_ReturnsFalseWithReason()
    {
        // Act
        var result = ShowStatusTransitions.CanEdit("Cancelled", out var reason);

        // Assert
        Assert.False(result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
