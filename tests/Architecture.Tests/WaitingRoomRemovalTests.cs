using System.Linq;
using Architecture.Tests.Support;
using Xunit;

namespace Architecture.Tests;

public class WaitingRoomRemovalTests
{
    [Fact]
    public void Inventory_service_assembly_has_no_waiting_room_types()
    {
        var inventoryAssembly = ServiceAssemblies.All.Single(a => a.GetName().Name == "Inventory.Service");

        var offendingTypes = inventoryAssembly.GetTypes()
            .Where(t => t.FullName != null && t.FullName.Contains("WaitingRoom"))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offendingTypes.Count == 0,
            "Inventory verifies admission tokens locally and must contain no waiting-room " +
            "types or references to the waiting-room queue table (ADR-002, ADR-020). Violations:\n" +
            string.Join("\n", offendingTypes));
    }
}
