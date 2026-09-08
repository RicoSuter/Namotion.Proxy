using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// A client writes into the node tree before the subject sees it, so an applied value is already at the
/// destination and must supersede an older local commit. Picking the other rule is silent: the node keeps
/// serving a value the model has moved past, and no transport test notices. Reads the rule back off a
/// constructed processor, so inlining a different value at the construction site fails this too.
/// </summary>
public class OpcUaServerDeliveryRuleTests
{
    [Fact]
    public void WhenTheServerCreatesItsProcessor_ThenItSelectsTheServerRule()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var subject = new DeliveryRuleTestRoot(context);
        var server = new OpcUaSubjectServer(subject, new OpcUaServerConfiguration(), NullLogger.Instance);

        // Act
        using var processor = server.CreateChangeQueueProcessor();

        // Assert
        Assert.Equal(ChangeDeliveryRule.SourceValuesAreSettled, processor.DeliveryRule);
    }

    /// <summary>
    /// The rule has a second use site, the re-check inside the node manager lock, which no unit test can
    /// reach because it needs a running server. Inlining a literal there passes everything outside the
    /// integration suite while the write loop drops exactly what it must write. Pins what the comment on
    /// the constant claims instead: the rule is named once, and every ranking call reads that name.
    /// </summary>
    [Fact]
    public void WhenTheServerRanksAChange_ThenEveryUseSiteReadsTheOneNamedRule()
    {
        // Arrange
        var lines = File.ReadAllLines(GetServerFilePath());
        const string declaration = "const ChangeDeliveryRule DeliveryRule";

        // Act
        var declarations = lines.Where(line => line.Contains(declaration)).ToArray();
        var inlinedRules = lines
            .Where(line => line.Contains("ChangeDeliveryRule.") && !line.Contains(declaration))
            .ToArray();
        var rankingCalls = lines.Where(line => line.Contains("ChangeDelivery.IsSuperseded(")).ToArray();

        // Assert
        Assert.Single(declarations);
        Assert.Empty(inlinedRules);
        Assert.NotEmpty(rankingCalls);
        // The exact argument, because "ChangeDeliveryRule.SourceValuesMayBeStale" also contains the
        // constant's name as a substring and would satisfy a looser check.
        Assert.All(rankingCalls, call => Assert.Contains(", DeliveryRule)", call));
    }

    private static string GetServerFilePath([CallerFilePath] string testFilePath = "")
    {
        // Resolved at compile time from this file's own path, so it survives whatever directory the test
        // runner happens to start in.
        var testDirectory = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "..", "Namotion.Interceptor.OpcUa", "Server", "OpcUaSubjectServer.cs"));
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class DeliveryRuleTestRoot
{
    public partial string? Name { get; set; }
}
