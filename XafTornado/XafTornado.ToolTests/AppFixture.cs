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

    public IReadOnlyList<AIFunction> Tools { get; }

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

        Tools = _factory.Services.GetRequiredService<AIToolsProvider>().Tools;
    }

    /// <summary>Invoke a tool by name with an anonymous-object argument bag; returns the parsed JSON result.</summary>
    public async Task<JsonNode> Invoke(string tool, object? args = null)
    {
        var fn = Tools.Single(f => f.Name == tool);
        var dict = args == null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await fn.InvokeAsync(new AIFunctionArguments(dict!));
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

    public void Dispose() => _factory.Dispose();
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
}
