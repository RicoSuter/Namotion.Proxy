using System.Runtime.CompilerServices;

using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PerPropertySubscriptionConventionsTests
{
    public PerPropertySubscriptionConventionsTests() => PropertyChangeSubscriptions.ResetForTests();

    // SubscribeToPath (PR #381) builds on per-property subscriptions; listed ahead of its arrival.
    private static readonly string[] SensitiveMarkers =
    [
        "PropertyChangeSubscriptions.",
        "PropertyChangeSubscription.Create",
        // Matches both SubscribeToPropertyInline and scheduled SubscribeToProperty as a substring.
        "SubscribeToProperty",
        "SubscribeToPath",
        "IPropertyChangeObserver",
        "PropertyChangeCallback",
        "ScheduledPropertySubscription",
        "SubscribeInline",
        // Scheduled callback overloads name none of the types above, so match both `in` lambda spellings.
        ".Subscribe((in ",
        ".Subscribe(static (in ",
    ];

    [Fact]
    public void WhenTestFileUsesPerPropertySubscriptionState_ThenItJoinsTheSerializedCollection()
    {
        // The subscription count is process-wide and xUnit runs collections in parallel: a test
        // creating per-property subscriptions outside the serialized collection intermittently
        // breaks the collection's count and idle-gate assertions, and the collection's
        // ResetForTests zeroes the count under the rogue test's live subscription, losing its
        // deliveries. Files that mention the collection type (including this one) pass.
        var offenders = Directory
            .EnumerateFiles(GetTestProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(file => (Name: Path.GetFileName(file), Content: File.ReadAllText(file)))
            .Where(file => SensitiveMarkers.Any(file.Content.Contains))
            .Where(file => !file.Content.Contains(nameof(PerPropertySubscriptionCollection)))
            .Select(file => file.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These test files use per-property subscriptions or the process-wide subscription count " +
            "but do not declare [Collection(PerPropertySubscriptionCollection.Name)]: " +
            $"{string.Join(", ", offenders)}. See the comment on PerPropertySubscriptionCollection.");
    }

    [Fact]
    public void WhenTestFileUsesPerPropertySubscriptionState_ThenItResetsProcessWideState()
    {
        // Arrange
        const string resetConstructorSuffix =
            "() => PropertyChangeSubscriptions.ResetForTests();";

        // Act
        var offenders = Directory
            .EnumerateFiles(GetTestProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(file => (Name: Path.GetFileName(file), Content: File.ReadAllText(file)))
            .Where(file => SensitiveMarkers.Any(file.Content.Contains))
            .Where(file =>
            {
                var className = Path.GetFileNameWithoutExtension(file.Name);
                var requiredResetConstructor = $"public {className}{resetConstructorSuffix}";
                return !file.Content
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => string.Equals(
                        line.Trim(),
                        requiredResetConstructor,
                        StringComparison.Ordinal));
            })
            .Select(file => file.Name)
            .ToArray();

        // Assert
        Assert.True(
            offenders.Length == 0,
            "These test files use per-property subscriptions or the process-wide subscription count " +
            "but do not use the standardized public expression-bodied reset constructor " +
            "'public <ClassName>() => PropertyChangeSubscriptions.ResetForTests();': " +
            $"{string.Join(", ", offenders)}. The convention requires one public test class named " +
            "for its file. Future fixture constructors require an explicit redesign of this convention.");
    }

    private static string GetTestProjectDirectory([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
}
