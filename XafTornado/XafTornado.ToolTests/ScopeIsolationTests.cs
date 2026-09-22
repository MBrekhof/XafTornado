using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using XafTornado.Blazor.Server.Services;
using XafTornado.Module.Services;

namespace XafTornado.ToolTests;

// SEC-002 / SEC-003: everything with per-user state is scoped, so two scopes (two Blazor circuits)
// never see each other's conversation, model, view context or queued navigation.

[Collection(nameof(AppCollection))]
public class ScopeIsolationTests(AppFixture app)
{
    [Fact]
    public void ConversationHistoryAndModel_AreNotSharedAcrossScopes()
    {
        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();
        var chatA = a.ServiceProvider.GetRequiredService<AIChatService>();
        var chatB = b.ServiceProvider.GetRequiredService<AIChatService>();

        chatA.LoadHistory([new("user", "hello"), new("assistant", "hi")]);
        chatA.CurrentModel = "gpt-4o-mini";

        Assert.NotSame(chatA, chatB);
        Assert.Empty(chatB.History);
        Assert.Empty(chatB.LastToolCalls);
        Assert.NotEqual("gpt-4o-mini", chatB.CurrentModel);

        chatA.Reset();
        Assert.Empty(chatA.History);
        Assert.NotEqual("gpt-4o-mini", chatA.CurrentModel);
    }

    [Fact]
    public void NavigationQueueAndViewContext_AreNotSharedAcrossScopes()
    {
        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();
        var navA = a.ServiceProvider.GetRequiredService<BlazorNavigationService>();
        var navB = b.ServiceProvider.GetRequiredService<BlazorNavigationService>();

        // The tool enqueues into its own scope's service ...
        Assert.Same(navA, a.ServiceProvider.GetRequiredService<INavigationService>());
        navA.FilterActiveList("[Country] = 'USA'");

        // ... and only that scope's executor can dequeue it.
        Assert.False(navB.TryDequeueFilter(out _));
        Assert.True(navA.TryDequeueFilter(out var request));
        Assert.Equal("[Country] = 'USA'", request.CriteriaString);

        var viewA = a.ServiceProvider.GetRequiredService<ActiveViewContext>();
        var viewB = b.ServiceProvider.GetRequiredService<ActiveViewContext>();
        viewA.Update("Customer", true, "Customer_ListView", typeof(object), null);
        Assert.NotSame(viewA, viewB);
        Assert.Null(viewB.EntityName);
    }

    [Fact]
    public void ChatClientAndTools_AreScoped_ApiIsShared()
    {
        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();

        Assert.NotSame(a.ServiceProvider.GetRequiredService<IChatClient>(), b.ServiceProvider.GetRequiredService<IChatClient>());
        Assert.NotSame(a.ServiceProvider.GetRequiredService<AIToolsProvider>(), b.ServiceProvider.GetRequiredService<AIToolsProvider>());
        Assert.Same(a.ServiceProvider.GetRequiredService<TornadoApiProvider>(), b.ServiceProvider.GetRequiredService<TornadoApiProvider>());
        Assert.Same(a.ServiceProvider.GetRequiredService<SchemaDiscoveryService>(), b.ServiceProvider.GetRequiredService<SchemaDiscoveryService>());
    }

    [Fact]
    public async Task BoundTools_TalkToTheirOwnScopesServices()
    {
        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();
        var toolsA = a.ServiceProvider.GetRequiredService<AIToolsProvider>().Tools;
        var toolsB = b.ServiceProvider.GetRequiredService<AIToolsProvider>().Tools;
        var navA = a.ServiceProvider.GetRequiredService<BlazorNavigationService>();
        var navB = b.ServiceProvider.GetRequiredService<BlazorNavigationService>();

        // A is looking at a customer list; B has no view.
        a.ServiceProvider.GetRequiredService<ActiveViewContext>().Update("Customer", true, "Customer_ListView", typeof(object), null);

        var viewA = await Invoke(toolsA, "get_active_view");
        var viewB = await Invoke(toolsB, "get_active_view");
        Assert.Equal("Customer", viewA["entity"]!.GetValue<string>());
        Assert.Equal("No active view context available.", viewB["error"]!.GetValue<string>());

        var filterA = await Invoke(toolsA, "filter_active_list", new { criteria = "[Country] = 'Germany'" });
        var filterB = await Invoke(toolsB, "filter_active_list", new { criteria = "[Country] = 'Germany'" });
        Assert.True(filterA["ok"]!.GetValue<bool>());
        Assert.StartsWith("No active list view", filterB["error"]!.GetValue<string>());

        // The request went into A's queue only.
        Assert.False(navB.TryDequeueFilter(out _));
        Assert.True(navA.TryDequeueFilter(out var request));
        Assert.Equal("[Country] = 'Germany'", request.CriteriaString);
    }

    [Fact]
    public async Task LogPanelTrace_IsPerScope()   // SEC-004
    {
        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();
        var logA = a.ServiceProvider.GetRequiredService<AILogScope>();
        var logB = b.ServiceProvider.GetRequiredService<AILogScope>();
        var toolsA = a.ServiceProvider.GetRequiredService<AIToolsProvider>().Tools;

        // A's tool call, with its arguments and result, lands in A's trace only.
        var result = await Invoke(toolsA, "query_entity", new { entityName = "Customer", filter = "Country=Germany" });
        Assert.Equal(3, result["count"]!.GetValue<int>());

        var entry = Assert.Single(logA.GetEntries());
        Assert.Equal("Tools", entry.Category);
        Assert.Contains("query_entity", entry.Message);
        Assert.Contains("Country=Germany", entry.Message);
        Assert.Contains("Alfreds Futterkiste", entry.Message);   // the result is the user's own to see
        Assert.Empty(logB.GetEntries());

        // A tool that answers { "error": ... } (the common failure path: bodies catch their own
        // exceptions) is a Warning entry; an exception that escapes the body is an Error entry.
        var unknown = await Invoke(toolsA, "query_entity", new { entityName = "Nope" });
        Assert.NotNull(unknown["error"]);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, logA.GetEntries().Last().Level);

        var fn = toolsA.Single(t => t.Name == "query_entity");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["entityName"] = "Order", ["top"] = "abc" })).AsTask());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, logA.GetEntries().Last().Level);

        // A cancelled call leaves no trace (its arguments belong to a discarded conversation).
        var before = logA.GetEntries().Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Invoke(toolsA, "describe_entity", new { entityName = "Order" }, new CancellationToken(canceled: true)));
        Assert.Equal(before, logA.GetEntries().Count);

        // A call that completed just before the scope was cleared (WinForms logoff between the UI
        // thread finishing the body and the caller resuming) must not repopulate the cleared trace.
        var providerA = a.ServiceProvider.GetRequiredService<AIToolsProvider>();
        providerA.Dispatch = async body => { var r = await body(); logA.Clear(); return r; };
        await Invoke(toolsA, "describe_entity", new { entityName = "Order" });
        Assert.Empty(logA.GetEntries());
        providerA.Dispatch = null;

        Assert.Empty(logB.GetEntries());
    }

    [Fact]
    public async Task Dispatch_NeverSeesAnException_TheCallerDoes()
    {
        using var scope = app.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<AIToolsProvider>();
        Exception? escaped = null;
        tools.Dispatch = async body =>
        {
            try { return await body(); }
            catch (Exception ex) { escaped = ex; throw; }   // would take a Blazor circuit down
        };

        // Argument binding fails before the tool body's own try/catch.
        var fn = tools.Tools.Single(t => t.Name == "query_entity");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["entityName"] = "Order", ["top"] = "abc" })).AsTask());

        Assert.Null(escaped);
    }

    [Fact]
    public async Task Dispatch_CancelledBeforeTheUiThreadRunsIt_DoesNotRunTheBody()
    {
        using var scope = app.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<AIToolsProvider>();
        var nav = scope.ServiceProvider.GetRequiredService<BlazorNavigationService>();
        var cts = new CancellationTokenSource();
        tools.Dispatch = body => { cts.Cancel(); return body(); };   // logoff/reset while queued for the UI thread

        // save_active_view takes no token itself; the dispatcher's recheck must stop it.
        var fn = tools.Tools.Single(t => t.Name == "save_active_view");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), cts.Token).AsTask());

        Assert.False(nav.ConsumeSave());
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> Invoke(IReadOnlyList<AIFunction> tools, string name, object? args = null, CancellationToken ct = default)
    {
        var dict = args == null
            ? new Dictionary<string, object?>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(System.Text.Json.JsonSerializer.Serialize(args))!;
        var result = await tools.Single(t => t.Name == name).InvokeAsync(new AIFunctionArguments(dict), ct);
        return System.Text.Json.Nodes.JsonNode.Parse(result!.ToString()!)!;
    }

    [Fact]
    public async Task Dispatch_RunsTheWholeToolBody_WhereThePlatformSaysSo()
    {
        using var scope = app.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<AIToolsProvider>();
        var dispatched = 0;
        tools.Dispatch = async body => { dispatched++; return await body(); };

        var fn = tools.Tools.Single(t => t.Name == "describe_entity");
        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["entityName"] = "Order" }));

        Assert.Equal(1, dispatched);
        Assert.Contains("\"name\":\"Order\"", result!.ToString());
    }
}
