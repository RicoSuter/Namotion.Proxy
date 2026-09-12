using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class ReadModeDemotionTests
{

    [Fact]
    public void NotificationMode_ShouldNeverBeDemoted_EvenWhenLimitIsZero()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new NotificationOnlyModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit of 0 notifications available
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 0);

        // Assert - Notification mode is protected, never demoted
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Notification, result[0].EffectiveMode);
    }

    [Fact]
    public void AutoMode_ShouldAllBeDemoted_WhenLimitIsZero()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - a limit of 0 leaves no notification budget for Auto
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 0);

        // Assert - this is what MaxNotifications = 0 is for: notifications only where asked for
        // explicitly. Unlike DefaultReadMode = Polled, it also covers an explicit Auto property.
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Polled, result[0].EffectiveMode);
    }

    [Fact]
    public void PolledMode_ShouldAlwaysRemainPolled_EvenWithPlentyOfSlots()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new PolledOnlyModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - plenty of notification slots available
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 1000);

        // Assert - Polled mode stays polled
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Polled, result[0].EffectiveMode);
    }

    [Fact]
    public void AutoMode_ShouldUseNotification_WhenWithinLimit()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - sufficient notification slots
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 100);

        // Assert - Auto mode gets notification when within limit
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Notification, result[0].EffectiveMode);
    }

    [Fact]
    public void AutoMode_ShouldDemoteToPolled_WhenLimitReached()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - no notification slots (limit of 0)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 0);

        // Assert - Auto mode demoted to polled
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Polled, result[0].EffectiveMode);
    }

    [Fact]
    public void DemotionOrder_ShouldDemoteHigherPriorityValueFirst()
    {
        // Arrange - 5 Auto mode properties with different priorities
        var context = TestHelpers.CreateContext();
        var model = new DemotionTestModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - allow only 2 notifications (3 must be demoted)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 2);

        // Assert - properties with highest Priority value should be demoted first
        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // Priority 10 (low priority, demoted first): SlowLowPriority and MediumLowPriority
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.SlowLowPriority"]);
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.MediumLowPriority"]);

        // Priority 0 (normal): SlowNormal should be demoted (higher CycleTime tiebreaker)
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.SlowNormal"]);

        // The remaining 2 should keep notification
        var notificationCount = result.Count(item => item.EffectiveMode == AdsReadMode.Notification);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public void DemotionOrder_ShouldUseHigherCycleTimeAsTiebreaker()
    {
        // Arrange - 5 Auto mode properties
        var context = TestHelpers.CreateContext();
        var model = new DemotionTestModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - allow 4 notifications (only 1 must be demoted)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 4);

        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // Among Priority=10, SlowLowPriority has CycleTime=1000 and MediumLowPriority has CycleTime=100
        // Higher CycleTime is demoted first, so SlowLowPriority gets demoted
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.SlowLowPriority"]);

        // MediumLowPriority should stay as notification (only 1 needed to demote)
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.MediumLowPriority"]);
    }

    [Fact]
    public void DemotionOrder_FastHighPriority_ShouldBeDemotedLast()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new DemotionTestModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - allow only 1 notification (4 must be demoted)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 1);

        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // FastHighPriority has Priority=-1 (highest priority, demoted last)
        // It should be the one that keeps notification
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.FastHighPriority"]);

        // All others should be demoted
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.FastNormal"]);
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.SlowNormal"]);
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.SlowLowPriority"]);
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.MediumLowPriority"]);
    }

    [Fact]
    public void MixedModes_ShouldOnlyDemoteAutoProperties()
    {
        // Arrange - Mix of Notification, Polled, and Auto
        var context = TestHelpers.CreateContext();
        var model = new MixedReadModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit of 1 notification
        // Notification + 2 Auto = 3 notification candidates
        // 1 is protected Notification, so only Auto properties can be demoted
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 1);

        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // Notification mode: always protected
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.NotificationVar"]);

        // Polled mode: always polled
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.PolledVar"]);

        // Both Auto vars should be demoted (Notification takes 1, limit is 1, so 0 slots for Auto)
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.AutoVar1"]);
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.AutoVar2"]);
    }

    [Fact]
    public void MixedModes_WithSufficientSlots_ShouldKeepAutoAsNotification()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new MixedReadModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit of 10 notifications (plenty)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 10);

        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // Notification mode: stays notification
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.NotificationVar"]);

        // Polled mode: stays polled
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.PolledVar"]);

        // Auto vars: should stay as notification (within limit)
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.AutoVar1"]);
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.AutoVar2"]);
    }

    [Fact]
    public void MixedModes_WithPartialDemotion_ShouldDemoteByPriorityThenCycleTime()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new MixedReadModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit of 2 notifications
        // NotificationVar (protected) takes 1 slot
        // AutoVar1 (Priority=0, CycleTime=50) and AutoVar2 (Priority=5, CycleTime=500) compete for 1 slot
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 2);

        var resultBySymbol = result.ToDictionary(
            item => item.SymbolPath,
            item => item.EffectiveMode);

        // AutoVar2 has higher Priority (5 > 0), so it gets demoted first
        Assert.Equal(AdsReadMode.Polled, resultBySymbol["GVL.AutoVar2"]);

        // AutoVar1 keeps notification
        Assert.Equal(AdsReadMode.Notification, resultBySymbol["GVL.AutoVar1"]);
    }

    [Fact]
    public void EmptyMappings_ShouldReturnEmptyResult()
    {
        // Arrange
        var mappings = Array.Empty<(RegisteredSubjectProperty, string, AdsPropertyMapping)>();

        // Act
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 500);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void DefaultReadMode_ShouldApplyWhenAttributeUsesAuto()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - default read mode is Polled (overrides Auto's default behavior)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Polled, 100, maxNotifications: 500);

        // Assert - defaultReadMode=Polled should make Auto properties become Polled
        Assert.Single(result);
        Assert.Equal(AdsReadMode.Polled, result[0].EffectiveMode);
    }

    [Fact]
    public void AllAutoProperties_WithLimitExceedingCount_ShouldNotDemoteAny()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new DemotionTestModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit way above count of properties
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 1000);

        // Assert - no demotion needed
        Assert.All(result, item => Assert.Equal(AdsReadMode.Notification, item.EffectiveMode));
    }

    [Fact]
    public void AllAutoProperties_WithExactLimit_ShouldNotDemoteAny()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new DemotionTestModel(context);
        var loader = TestHelpers.CreateLoader();
        var mappings = loader.LoadSubjectGraph(model);

        // Act - limit exactly equals count of properties (5)
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, maxNotifications: 5);

        // Assert - no demotion needed (exactly at limit)
        Assert.All(result, item => Assert.Equal(AdsReadMode.Notification, item.EffectiveMode));
    }
}
