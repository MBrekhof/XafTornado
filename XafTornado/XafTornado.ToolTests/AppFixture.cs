using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.ExpressApp.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using XafTornado.Blazor.Server;
using XafTornado.Module.Services;

namespace XafTornado.ToolTests;

/// <summary>
/// Boots the real Blazor Server host (full XAF DI graph) against a throwaway PostgreSQL
/// database that is dropped, recreated and seeded once per test run. Tools are invoked
/// through <see cref="AIFunction.InvokeAsync"/> with JSON-deserialized arguments — the same
/// path <c>AIChatService</c> uses when the model calls a tool.
/// </summary>
public sealed class AppFixture : IDisposable
{
    // Override with XAFTORNADO_TEST_PG="Host=...;Port=...;Username=...;Password=..."
    private static readonly string PgServer =
        Environment.GetEnvironmentVariable("XAFTORNADO_TEST_PG")
        ?? "Host=localhost;Port=5432;Username=xaf;Password=xaf123";

    private const string DbName = "xaftornado_test";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly IServiceScope _scope;

    /// <summary>The tools of one retained scope (the AI services are scoped per user).</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>Root provider, for tests that need scopes of their own.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// The fixture scope's fake UI executor: records every request and answers with
    /// <see cref="UiOutcome"/> (Success unless a test sets a failure). Headless tests have no
    /// window, and a tool must never say "ok" on its own (AI-007).
    /// </summary>
    public List<UiRequest> UiRequests { get; } = new();
    public NavigationResult UiOutcome { get; set; } = NavigationResult.Success;

    public AppFixture()
    {
        RecreateDatabase();

        var xafConnectionString = $"EFCoreProvider=PostgreSQL;{PgServer};Database={DbName};Persist Security Info=True";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ConnectionString"] = xafConnectionString,
            })));

        // Same as `--updateDatabase --forceUpdate --silent`: creates schema + seed data.
        using (var scope = _factory.Services.CreateScope())
        {
            var status = scope.ServiceProvider.GetRequiredService<IDBUpdater>().Update(forceUpdate: true, silent: true);
            if (status == 1) // 0 completed, 1 error, 2 not needed (see Program.cs --help)
                throw new InvalidOperationException("Database update failed.");
        }

        _scope = _factory.Services.CreateScope();
        Tools = _scope.ServiceProvider.GetRequiredService<AIToolsProvider>().Tools;
        AttachFakeExecutor(_scope.ServiceProvider.GetRequiredService<NavigationRequestQueue>(), UiRequests, () => UiOutcome);
    }

    /// <summary>Gives a scope's queue an executor that records requests and answers with <paramref name="outcome"/>.</summary>
    public static void AttachFakeExecutor(NavigationRequestQueue queue, List<UiRequest> recorded, Func<NavigationResult> outcome)
    {
        queue.OnRequest += () =>
        {
            while (queue.TryDequeue(out var request))
            {
                recorded.Add(request);
                request.Outcome = outcome();
                request.MarkDone();
            }
        };
    }

    /// <summary>Invoke a tool by name with an anonymous-object argument bag; returns the parsed JSON result.</summary>
    public async Task<JsonNode> Invoke(string tool, object? args = null, CancellationToken cancellationToken = default)
    {
        var fn = Tools.Single(f => f.Name == tool);
        var dict = args == null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await fn.InvokeAsync(new AIFunctionArguments(dict!), cancellationToken);
        var text = result?.ToString() ?? throw new InvalidOperationException($"{tool} returned null");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{tool} returned non-JSON: {text}");
    }

    private static void RecreateDatabase()
    {
        using var conn = new NpgsqlConnection($"{PgServer};Database=postgres");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS {DbName} WITH (FORCE); CREATE DATABASE {DbName};";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _factory.Dispose();
    }
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
}
