using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the property history chart dialog.
/// </summary>
/// <remarks>
/// The chart's arithmetic is covered far more cheaply by PropertyHistoryChartModelTests. What only a
/// browser can answer is whether the dialog reaches a store at all: the recorder, the registry lookup,
/// the coverage query and the chart binding are wired across four assemblies, and every one of them
/// can be individually correct while the dialog still opens empty. The truncation case is here because
/// it went unnoticed until it was seen running: an auto bucket sized from a store that had just started
/// recording asked for more intervals than the query returns, so a nearly empty chart reported holding
/// too much history.
/// </remarks>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PropertyHistoryDialogTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 15000;
    private readonly PlaywrightFixture _fixture;

    public PropertyHistoryDialogTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenMotorHistoryAsync(string propertyLabel)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = PageLoadTimeout });

        // Scoped to the child list's typography: an unscoped "Demo" also matches the app bar's page link.
        await page.Locator("span:text-is('Demo')").First.ClickAsync();
        await page.Locator("span:text-is('Test Motor')").First.ClickAsync();

        // The property renders as a link only when it is recordable AND a history store is registered,
        // so this click is itself the assertion that the store reached the registry.
        var propertyLink = page.Locator($"a[title='Show history']:has-text('{propertyLabel}')").First;
        await Assertions.Expect(propertyLink).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await propertyLink.ClickAsync();

        var dialog = page.Locator("[data-testid='property-history-dialog']");
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        return page;
    }

    [Fact]
    public async Task WhenAPropertyHasHistory_ThenItsLinkOpensTheChartDialog()
    {
        // Arrange & Act
        var page = await OpenMotorHistoryAsync("Speed");

        // Assert - the controls sit outside the loading and error guards, so this does not prove the query
        // succeeded. What it does prove is the reach of the wiring: the property renders as a link at all
        // only because a store registered itself and claimed the path, which is the click above.
        var dialog = page.Locator("[data-testid='property-history-dialog']");
        await Assertions.Expect(dialog.Locator("label:text-is('Range')")).ToBeVisibleAsync(
            new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(dialog.Locator("label:text-is('Period')")).ToBeVisibleAsync(
            new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheStoreHasJustStartedRecording_ThenNoTruncationNoticeIsShown()
    {
        // Arrange - the store began recording when the host started, seconds ago, while the dialog opens
        // on a far wider default range. That is exactly the shape that made the auto bucket collapse to
        // the ladder floor and demand thousands of intervals for a cap of a thousand.
        var page = await OpenMotorHistoryAsync("Speed");

        // Act
        var notice = page.Locator("[data-testid='history-truncation-notice']");

        // Assert - nothing was dropped, so the notice must be absent. A chart holding a few seconds of
        // samples has no oldest data to hide.
        await Assertions.Expect(notice).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenItReportsNoQueryError()
    {
        // Arrange - the dialog swallows a failed query into an alert rather than throwing, so a store
        // that is registered but unreadable still opens a dialog. Without this the smoke tests above
        // would pass against a chart that had failed to load anything.
        var page = await OpenMotorHistoryAsync("Speed");

        // Act
        var alert = page.Locator("[data-testid='property-history-dialog'] .mud-alert");

        // Assert
        await Assertions.Expect(alert).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }
}
