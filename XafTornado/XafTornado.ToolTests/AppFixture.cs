using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Utils;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using XafTornado.Blazor.Server;
using XafTornado.Blazor.Server.Controllers;
using XafTornado.Module.BusinessObjects;
using XafTornado.Module.Services;

namespace XafTornado.ToolTests;

/// <summary>
/// Boots the real Blazor Server host (full XAF DI graph) against a throwaway PostgreSQL
/// database that is dropped, recreated and seeded once per test run. Tools are invoked
/// through <see cref="AIFunction.InvokeAsync"/> with JSON-deserialized arguments — the same
/// path <c>AIChatService</c> uses when the model calls a tool. The AI services are scoped per
/// user, and tools read through that user's secured object space (SEC-001), so every scope here
/// has a user logged on: <see cref="Tools"/> is Admin, <see cref="ToolsAs"/> gives another user.
/// </summary>
public sealed class AppFixture : IDisposable
{
    // Override with XAFTORNADO_TEST_PG="Host=...;Port=...;Username=...;Password=..."
    private static readonly string PgServer =
        Environment.GetEnvironmentVariable("XAFTORNADO_TEST_PG")
        ?? "Host=localhost;Port=5432;Username=xaf;Password=xaf123";

    private const string DbName = "xaftornado_test";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Dictionary<string, (IServiceScope Scope, IDisposable SignIn, IReadOnlyList<AIFunction> Tools)> _userScopes = new();

    /// <summary>Admin's tools (the retained Admin scope).</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>Root provider, for tests that need scopes of their own.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// The Admin scope's fake UI executor: records every request and answers with
    /// <see cref="UiOutcome"/> (Success unless a test sets a failure). Headless tests have no
    /// window, and a tool must never say "ok" on its own (AI-007).
    /// </summary>
    public List<UiRequest> UiRequests { get; } = new();
    public NavigationResult UiOutcome { get; set; } = NavigationResult.Success;

    // Users created by the fixture for the permission tests (all with an empty password):
    //   reader   - Reader role:   read Customer, nothing else
    //   germany  - Germany role:  read Customer rows where Country = 'Germany'
    //   nophone  - NoPhone role:  read Customer, but not the Phone member
    // "User" (seeded Default role) can read nothing; "Admin" everything.
    public const string Reader = "reader", Germany = "germany", NoPhone = "nophone";

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
            CreatePermissionTestUsers(scope.ServiceProvider);
        }

        Tools = ToolsAs("Admin");
        AttachFakeExecutor(_userScopes["Admin"].Scope.ServiceProvider.GetRequiredService<NavigationRequestQueue>(), UiRequests, () => UiOutcome);
    }

    /// <summary>The tools of a retained scope with <paramref name="userName"/> logged on.</summary>
    public IReadOnlyList<AIFunction> ToolsAs(string userName)
    {
        if (_userScopes.TryGetValue(userName, out var existing)) return existing.Tools;
        var scope = _factory.Services.CreateScope();
        var signIn = TestApiController.SignIn(scope.ServiceProvider, userName);
        var security = scope.ServiceProvider.GetRequiredService<ISecurityStrategyBase>();
        if ((security.User as ISecurityUser)?.UserName != userName)
            throw new InvalidOperationException($"Scope is not logged on as {userName}.");
        var tools = scope.ServiceProvider.GetRequiredService<AIToolsProvider>().Tools;
        _userScopes[userName] = (scope, signIn, tools);
        return tools;
    }

    /// <summary>Invoke a tool by name with an anonymous-object argument bag; returns the parsed JSON result.</summary>
    public Task<JsonNode> Invoke(string tool, object? args = null, CancellationToken cancellationToken = default) =>
        Invoke(Tools, tool, args, cancellationToken);

    /// <summary>Same as <see cref="Invoke(string, object?, CancellationToken)"/>, as another user.</summary>
    public Task<JsonNode> InvokeAs(string userName, string tool, object? args = null) => Invoke(ToolsAs(userName), tool, args);

    public static async Task<JsonNode> Invoke(IReadOnlyList<AIFunction> tools, string tool, object? args = null, CancellationToken cancellationToken = default)
    {
        var fn = tools.Single(f => f.Name == tool);
        var dict = args == null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await fn.InvokeAsync(new AIFunctionArguments(dict!), cancellationToken);
        var text = result?.ToString() ?? throw new InvalidOperationException($"{tool} returned null");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{tool} returned non-JSON: {text}");
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

    private static void CreatePermissionTestUsers(IServiceProvider services)
    {
        using var os = services.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace<ApplicationUser>();
        var users = services.GetRequiredService<UserManager>();
        if (users.FindUserByName<ApplicationUser>(os, Reader) != null) return;

        var reader = os.CreateObject<PermissionPolicyRole>();
        reader.Name = "Reader";
        reader.AddTypePermissionsRecursively<Customer>(SecurityOperations.Read, SecurityPermissionState.Allow);

        var germany = os.CreateObject<PermissionPolicyRole>();
        germany.Name = "Germany";
        germany.AddObjectPermissionFromLambda<Customer>(SecurityOperations.Read, c => c.Country == "Germany", SecurityPermissionState.Allow);

        var noPhone = os.CreateObject<PermissionPolicyRole>();
        noPhone.Name = "NoPhone";
        noPhone.AddTypePermissionsRecursively<Customer>(SecurityOperations.Read, SecurityPermissionState.Allow);
        noPhone.AddMemberPermission<Customer>(SecurityOperations.Read, nameof(Customer.Phone), null, SecurityPermissionState.Deny);

        users.CreateUser<ApplicationUser>(os, Reader, "", u => u.Roles.Add(reader));
        users.CreateUser<ApplicationUser>(os, Germany, "", u => u.Roles.Add(germany));
        users.CreateUser<ApplicationUser>(os, NoPhone, "", u => u.Roles.Add(noPhone));
        os.CommitChanges();
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
        foreach (var (scope, signIn, _) in _userScopes.Values)
        {
            signIn.Dispose();
            scope.Dispose();
        }
        _factory.Dispose();
    }
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
}
