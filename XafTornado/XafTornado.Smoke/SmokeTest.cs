using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace XafTornado.Smoke;

/// <summary>
/// One end-to-end smoke test against a running Blazor Server app (Debug build — the tool
/// call goes through the Debug-only TestApiController). Does not host the app: run
/// <c>scripts/smoke.ps1</c>, which updates the DB, starts the app, runs this, and stops it.
/// Catches what the build cannot: startup, DB connection, login, list rendering, AI panel,
/// tool wiring — the two things that broke in the DX 26.1 / EF Core 10 upgrade.
/// </summary>
public class SmokeTest : PageTest
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("XAFTORNADO_BASE_URL") ?? "http://localhost:5000";

    public override BrowserNewContextOptions ContextOptions() => new()
    {
        ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
        IgnoreHTTPSErrors = true,
    };

    [Test]
    public async Task Login_ListView_AiPanel_ToolCall()
    {
        Page.SetDefaultTimeout(20_000);

        // 1. Login page renders (Blazor circuit up, DB reachable for the login action)
        await Page.GotoAsync($"{BaseUrl}/LoginPage", new() { WaitUntil = WaitUntilState.NetworkIdle });
        var userName = Page.GetByRole(AriaRole.Textbox, new() { Name = "User Name" });
        await Expect(userName).ToBeVisibleAsync();
        await userName.FillAsync("Admin");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();

        // 2. Logged in: a list view with data (catches schema mismatch / password-less connection)
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/LoginPage"));
        var gridStatus = Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Data grid with" });
        await Expect(gridStatus).Not.ToHaveTextAsync(new Regex(@"Data grid with 0 rows"));

        // 3. AI panel is mounted and accepts input (DxAIChat resolved IChatClient from DI)
        var chatInput = Page.GetByRole(AriaRole.Textbox, new() { Name = "Type your message here" });
        if (!await chatInput.IsVisibleAsync())
            await Page.GetByRole(AriaRole.Button, new() { Name = "✨" }).ClickAsync();
        await Expect(chatInput).ToBeVisibleAsync();

        // 4. One real tool call through the running process (AIToolsProvider + ObjectSpace + Npgsql)
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var response = await http.PostAsJsonAsync("/api/test/tool",
            new { tool = "query_entity", @params = new { entityName = "Order", top = 1 } });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = JsonDocument.Parse(body.GetProperty("result").GetString()!).RootElement;
        Assert.That(result.TryGetProperty("error", out var err) ? err.GetString() : null, Is.Null);
        Assert.That(result.GetProperty("count").GetInt32(), Is.EqualTo(1));
    }

    [TearDown]
    public async Task ScreenshotOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed) return;
        var path = Path.Combine(AppContext.BaseDirectory, $"smoke-failure-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
        TestContext.AddTestAttachment(path);
        TestContext.Out.WriteLine($"Screenshot: {path}");
    }
}
