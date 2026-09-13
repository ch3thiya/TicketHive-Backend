using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

public class TimeProviderUsageTests
{
    private static readonly Regex DirectDateTimeUsage = new(@"\bDateTime\.(UtcNow|Now)\b");

    [Fact]
    public void No_direct_DateTime_usage_outside_BuildingBlocks()
    {
        var servicesRoot = FindServicesRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (ShouldSkip(file, servicesRoot))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (DirectDateTimeUsage.IsMatch(line))
                {
                    violations.Add($"{Path.GetRelativePath(servicesRoot, file)}:{lineNumber}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Time comes from an injected TimeProvider, not DateTime.Now/UtcNow directly, " +
            "outside BuildingBlocks (ADR-015). Violations:\n" + string.Join("\n", violations));
    }

    private static bool ShouldSkip(string file, string servicesRoot)
    {
        var relativeSegments = Path.GetRelativePath(servicesRoot, file)
            .Split(Path.DirectorySeparatorChar);

        return relativeSegments[0] == "BuildingBlocks"
            || relativeSegments.Contains("bin")
            || relativeSegments.Contains("obj")
            || Path.GetFileName(file) == "Program.cs"
            || file.EndsWith(".g.cs", StringComparison.Ordinal);
    }

    private static string FindServicesRoot([CallerFilePath] string thisFilePath = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(thisFilePath))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "services");
    }
}
