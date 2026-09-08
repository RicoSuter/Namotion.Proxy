using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for SubjectSetupDialog (Create Subject Wizard) functionality.
/// Tests type selection, name input, wizard navigation, and validation.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectSetupDialogTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 5000;

    private readonly PlaywrightFixture _fixture;

    public SubjectSetupDialogTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    #region Helper Methods

    /// <summary>
    /// The Create operation of the subject pane the wizard is opened from. Scoped to the pane
    /// container so it cannot resolve to the wizard's own Create button on step 2.
    /// </summary>
    private static ILocator PaneCreateButton(IPage page)
    {
        return page.Locator("#scrollContainer button:has-text('Create')").First;
    }

    private async Task OpenSubjectBrowserAsync(IPage page)
    {
        await page.GotoAsync($"{_fixture.ServerAddress}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = PageLoadTimeout });

        // The URL changes before the browser has built its panes, and the pane's Create operation is
        // what the wizard is opened from, so that button is the condition the next step depends on.
        var createButton = PaneCreateButton(page);
        await Assertions.Expect(createButton).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
    }

    private async Task OpenWizardAsync(IPage page)
    {
        await PaneCreateButton(page).ClickAsync();

        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // The dialog frame appears before its content, and Step1_OnOpen counts type cards with
        // CountAsync, which does not retry, so the cards are the condition to hold for.
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await Assertions.Expect(firstTypeCard).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    private async Task EnterNameAsync(IPage page, string name)
    {
        var nameInput = page.GetByLabel("Name");
        await nameInput.FillAsync(name);

        // The field binds immediately and the circuit delivers events in order, so a later click
        // cannot overtake the name this fill sends. This assertion only catches Blazor
        // reverting the field; it is not a round trip to the server.
        await Assertions.Expect(nameInput).ToHaveValueAsync(name, new() { Timeout = ElementVisibilityTimeout });
    }

    private async Task SelectTypeAsync(IPage page, string typeTestId)
    {
        var typeCard = page.Locator($"[data-testid='{typeTestId}']");
        await typeCard.ClickAsync();
    }

    private async Task SelectFirstTypeAsync(IPage page)
    {
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();
    }

    private async Task AdvanceToStep2Async(IPage page, string name = "testsubject")
    {
        await EnterNameAsync(page, name);
        await SelectFirstTypeAsync(page);

        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    #endregion

    #region Step 1 Tests

    [Fact]
    public async Task Step1_OnOpen_ShowsExpectedUIElements()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Wizard dialog should be visible
        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Step 1 title visible
        var step1Title = page.GetByText("Step 1:");
        await Assertions.Expect(step1Title).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Name input should be visible
        var nameInput = page.Locator("[data-testid='create-name-input']");
        await Assertions.Expect(nameInput).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // At least one category panel should be visible
        var categoryPanels = page.Locator("[data-testid^='category-panel-']");
        var categoryCount = await categoryPanels.CountAsync();
        Assert.True(categoryCount > 0, "At least one category panel should be visible");

        // Type cards should be visible (confirms accordions are expanded)
        var typeCards = page.Locator("[data-testid^='type-card-']");
        var typeCount = await typeCards.CountAsync();
        Assert.True(typeCount > 0, "At least one type card should be visible");

        // Create button should NOT be visible on step 1
        var createButton = page.Locator("[data-testid='create-button']");
        await Assertions.Expect(createButton).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Cancel button should be visible
        var cancelButton = page.Locator("[data-testid='cancel-button']");
        await Assertions.Expect(cancelButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task Step1_TypeSelection_ShowsNextButtonAndHighlight()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Click type card without entering name
        var motorCard = page.Locator("[data-testid='type-card-motor']");
        await motorCard.ClickAsync();

        // Type card should be highlighted (has mud-border-primary class)
        await Assertions.Expect(motorCard).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-border-primary"));

        // Next button should be visible but disabled (no name)
        var nextButton = page.Locator("[data-testid='next-button']");
        await Assertions.Expect(nextButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(nextButton).ToBeDisabledAsync();

        // Enter name - Next button should become enabled
        await EnterNameAsync(page, "mytest");
        await Assertions.Expect(nextButton).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task Step1_EnterNameAndSelectType_AutoAdvancesToStep2()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Enter name first, then select type - should auto-advance
        await EnterNameAsync(page, "mytest");
        await SelectTypeAsync(page, "type-card-motor");

        // Should be on step 2 (Back button visible, title shows type name)
        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        var step2Title = page.GetByText("Step 2: Create Motor");
        await Assertions.Expect(step2Title).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    #endregion

    #region Step 2 Tests

    [Fact]
    public async Task Step2_OnAdvance_ShowsConfigurationAndCreateButton()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Advance to step 2 with Motor type
        await EnterNameAsync(page, "testmotor");
        await SelectTypeAsync(page, "type-card-motor");

        // Wait for step 2
        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Title should show type name
        var step2Title = page.GetByText("Step 2: Create Motor");
        await Assertions.Expect(step2Title).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Create button should be visible and enabled
        var createButton = page.Locator("[data-testid='create-button']");
        await Assertions.Expect(createButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(createButton).ToBeEnabledAsync();

        // Should show configuration panel or "No configuration needed"
        var noConfigMessage = page.Locator("text='No configuration needed'");
        var hasMessage = await noConfigMessage.IsVisibleAsync();
        if (!hasMessage)
        {
            var hasInputs = await page.Locator("input, select, .mud-input").CountAsync() > 0;
            Assert.True(hasInputs, "Step 2 should show either configuration or 'No configuration needed' message");
        }
    }

    #endregion

    #region Navigation Tests

    [Fact]
    public async Task Navigation_CancelButton_ClosesDialog()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        var cancelButton = page.Locator("[data-testid='cancel-button']");
        await cancelButton.ClickAsync();

        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task Navigation_BackButton_ReturnsToStep1AndPreservesState()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Enter a specific name and advance to step 2
        var testName = "mypreservedname";
        await EnterNameAsync(page, testName);
        await SelectTypeAsync(page, "type-card-motor");

        // Wait for step 2
        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Click Back
        await backButton.ClickAsync();

        // Should be back on step 1
        var step1Title = page.GetByText("Step 1:");
        await Assertions.Expect(step1Title).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Back button should no longer be visible
        await Assertions.Expect(backButton).Not.ToBeVisibleAsync();

        // Name should be preserved
        var nameInput = page.GetByLabel("Name");
        await Assertions.Expect(nameInput).ToHaveValueAsync(testName);

        // Type selection should be preserved (Motor card still highlighted)
        var motorCard = page.Locator("[data-testid='type-card-motor']");
        await Assertions.Expect(motorCard).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-border-primary"));
    }

    #endregion

    #region Validation Tests

    [Fact]
    public async Task Validation_NameErrors_PreventAdvancing()
    {
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await OpenWizardAsync(page);

        // Test 1: Empty name shows required error
        var nameInput = page.GetByLabel("Name");
        await nameInput.FillAsync("temp");
        await nameInput.FillAsync("");
        await nameInput.BlurAsync();

        var requiredError = page.Locator(".mud-input-helper-text:has-text('Name is required')");
        await Assertions.Expect(requiredError).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Test 2: Invalid characters show error
        await nameInput.FillAsync("test/invalid:name");
        await nameInput.BlurAsync();

        var invalidError = page.Locator(".mud-input-helper-text:has-text('Name contains invalid characters')");
        await Assertions.Expect(invalidError).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Next button should be disabled even with type selected
        await SelectFirstTypeAsync(page);
        var nextButton = page.Locator("[data-testid='next-button']");
        await Assertions.Expect(nextButton).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Test 3: Valid name enables Next button
        await nameInput.FillAsync("validname");
        await Assertions.Expect(nextButton).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    #endregion
}
