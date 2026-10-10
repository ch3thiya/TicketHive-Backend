using Xunit;
using Identity.Service.Models;

namespace Identity.Service.Tests;

public class OrganizerStatusRulesTests
{
    [Theory]
    [InlineData("approved", OrganizerStatusAction.Suspend, OrganizerStatusChangeOutcome.Changed, "suspended")]
    [InlineData("suspended", OrganizerStatusAction.Suspend, OrganizerStatusChangeOutcome.Unchanged, "suspended")]
    [InlineData("suspended", OrganizerStatusAction.Reinstate, OrganizerStatusChangeOutcome.Changed, "approved")]
    [InlineData("approved", OrganizerStatusAction.Reinstate, OrganizerStatusChangeOutcome.Unchanged, "approved")]
    public void Decide_KnownState_ReturnsExpectedOutcome(string current, OrganizerStatusAction action, OrganizerStatusChangeOutcome outcome, string target)
    {
        // Act
        var decision = OrganizerStatusRules.Decide(current, action);

        // Assert
        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(target, decision.TargetStatus);
    }

    [Theory]
    [InlineData("pending", OrganizerStatusAction.Suspend)]
    [InlineData("rejected", OrganizerStatusAction.Suspend)]
    [InlineData("pending", OrganizerStatusAction.Reinstate)]
    [InlineData("rejected", OrganizerStatusAction.Reinstate)]
    public void Decide_NeverApprovedOrganizer_IsInvalidTransition(string current, OrganizerStatusAction action)
    {
        // Act
        var decision = OrganizerStatusRules.Decide(current, action);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.InvalidTransition, decision.Outcome);
        Assert.Null(decision.TargetStatus);
    }
}