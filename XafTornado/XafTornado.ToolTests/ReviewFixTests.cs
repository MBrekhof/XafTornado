using System.Net;
using System.Reflection;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;
using XafTornado.Blazor.Server.Controllers;
using XafTornado.Module.Services;

namespace XafTornado.ToolTests;

// Regression tests for the 2026-09-22 Codex review quick fixes. Seed data pairs used below:
// customers "Bólido Comidas" / "Cactus Comidas" and "Blauer See Delikatessen" / "Drachenblut Delikatessen".

[Collection(nameof(AppCollection))]
public class AmbiguousMatchTests(AppFixture app)   // AI-006
{
    [Fact]
    public async Task CreateEntity_AmbiguousReference_ReturnsCandidates_AndCreatesNothing()
    {
        var before = (await app.Invoke("query_entity", new { entityName = "Order", top = 1000 }))["count"]!.GetValue<int>();

        var r = await app.Invoke("create_entity", new { entityName = "Order", properties = "Customer=Comidas;OrderDate=2026-09-22" });

        Assert.Contains("ambiguous", r["error"]!.GetValue<string>());
        var names = r["candidates"]!.AsArray().Select(c => c!["display"]!.GetValue<string>()).ToList();
        Assert.Contains("Bólido Comidas", names);
        Assert.Contains("Cactus Comidas", names);
        Assert.All(r["candidates"]!.AsArray(), c => Assert.True(Guid.TryParse(c!["id"]!.GetValue<string>(), out _)));

        var after = (await app.Invoke("query_entity", new { entityName = "Order", top = 1000 }))["count"]!.GetValue<int>();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UpdateEntity_AmbiguousIdentifier_ReturnsCandidates_AndChangesNothing()
    {
        var r = await app.Invoke("update_entity", new { entityName = "Customer", identifier = "Delikatessen", properties = "City=Nowhere" });

        Assert.Contains("ambiguous", r["error"]!.GetValue<string>());
        var names = r["candidates"]!.AsArray().Select(c => c!["display"]!.GetValue<string>()).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("Blauer See Delikatessen", names);
        Assert.Contains("Drachenblut Delikatessen", names);

        var q = await app.Invoke("query_entity", new { entityName = "Customer", filter = "City=Nowhere" });
        Assert.Equal(0, q["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateEntity_ExactName_WinsOverSubstringMatch()
    {
        // "Horn" is a substring of the seeded "Around the Horn" (inserted first) and, after this,
        // the exact name of a second customer. First-substring-wins would pick Around the Horn.
        var created = await app.Invoke("create_entity", new { entityName = "Customer", properties = "CompanyName=Horn;Country=Norway" });
        Assert.True(created["created"]!.GetValue<bool>());

        var r = await app.Invoke("update_entity", new { entityName = "Customer", identifier = "Horn", properties = "City=Bergen" });

        Assert.True(r["updated"]!.GetValue<bool>());
        Assert.Equal("Horn", r["display"]!.GetValue<string>());
        Assert.Equal(created["id"]!.GetValue<string>(), r["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateEntity_ReferenceById_ResolvesWithoutNameMatching()
    {
        var beverages = (await app.Invoke("query_entity", new { entityName = "Category", filter = "Name=Beverages" }))["records"]!.AsArray().Single()!;
        var id = beverages["id"]!.GetValue<string>();

        var r = await app.Invoke("create_entity", new { entityName = "Product", properties = $"Name=Test By Id;UnitPrice=2;UnitsInStock=1;Category={id}" });

        Assert.True(r["created"]!.GetValue<bool>());
        Assert.Equal("Beverages", r["values"]!["Category"]!.GetValue<string>());
    }

    [Fact]
    public async Task NavigateToDetail_AmbiguousSearchTerm_ReturnsCandidates()
    {
        var r = await app.Invoke("navigate_to_detail", new { entityName = "Customer", identifier = "Comidas" });

        Assert.Contains("ambiguous", r["error"]!.GetValue<string>());
        Assert.Equal(2, r["candidates"]!.AsArray().Count);
    }

    [Fact]
    public async Task NavigateToDetail_ExactName_ResolvesToId()
    {
        var r = await app.Invoke("navigate_to_detail", new { entityName = "Customer", identifier = "Bólido Comidas" });

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(Guid.TryParse(r["id"]!.GetValue<string>(), out _));
        Assert.Equal("Bólido Comidas", r["display"]!.GetValue<string>());
    }

    [Fact]
    public async Task NavigateToDetail_UnknownRecord_ReturnsNotFound()
    {
        var r = await app.Invoke("navigate_to_detail", new { entityName = "Customer", identifier = "No Such Company" });

        Assert.StartsWith("No Customer record found", r["error"]!.GetValue<string>());
    }
}

[Collection(nameof(AppCollection))]
public class CancellationTests(AppFixture app)   // AI-008
{
    [Fact]
    public async Task CreateEntity_CancelledToken_ThrowsAndCommitsNothing()
    {
        var before = (await app.Invoke("query_entity", new { entityName = "Category", top = 1000 }))["count"]!.GetValue<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.Invoke("create_entity", new { entityName = "Category", properties = "Name=Never Created" }, new CancellationToken(canceled: true)));

        var after = (await app.Invoke("query_entity", new { entityName = "Category", top = 1000 }))["count"]!.GetValue<int>();
        Assert.Equal(before, after);
    }
}

[Collection(nameof(AppCollection))]
public class SchemaDiscoveryTests   // AI-005
{
    [Fact]
    public void InvalidateCache_AfterEmptyDiscovery_RespectsOptIn()
    {
        // WinForms shape: first discovery runs before Setup() against an empty ITypesInfo ...
        var typesInfo = new TypesInfoStub();
        var service = new SchemaDiscoveryService(typesInfo);
        Assert.Empty(service.Schema.Entities);

        // ... then the real types arrive and the cache is invalidated.
        typesInfo.PersistentTypes = XafTypesInfo.Instance.PersistentTypes.ToList();
        service.InvalidateCache();

        var names = service.Schema.Entities.Select(e => e.Name).ToList();
        Assert.Contains("Customer", names);
        Assert.DoesNotContain("ApplicationUser", names);
        Assert.DoesNotContain("ApplicationUserLoginInfo", names);
    }

    private sealed class TypesInfoStub : ITypesInfo
    {
        public IEnumerable<ITypeInfo> PersistentTypes { get; set; } = [];

        public void RegisterEntity(Type entityType) => throw new NotSupportedException();
        public ITypeInfo FindTypeInfo(Type type) => throw new NotSupportedException();
        public ITypeInfo FindTypeInfo(string typeName) => throw new NotSupportedException();
        public bool CanInstantiate(Type type) => throw new NotSupportedException();
        public void RefreshInfo(ITypeInfo info) => throw new NotSupportedException();
        public void RefreshInfo(Type type) => throw new NotSupportedException();
        public IAssemblyInfo FindAssemblyInfo(Type ofType) => throw new NotSupportedException();
        public IAssemblyInfo FindAssemblyInfo(Assembly assembly) => throw new NotSupportedException();
        public void LoadTypes(Assembly assembly) => throw new NotSupportedException();
        public IMemberInfo CreatePath(IMemberInfo first, IMemberInfo second) => throw new NotSupportedException();
    }
}

#if DEBUG
public class LoopbackOnlyTests   // SEC-005
{
    [Theory]
    [InlineData("10.0.0.5", 403)]
    [InlineData("2001:db8::1", 403)]
    [InlineData("127.0.0.1", null)]
    [InlineData("::1", null)]
    [InlineData(null, null)]   // in-process test host: no remote address
    public void OnlyLoopbackCallersPass(string? remoteIp, int? expectedStatus)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = remoteIp == null ? null : IPAddress.Parse(remoteIp);
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        new LoopbackOnlyAttribute().OnActionExecuting(context);

        if (expectedStatus == null)
            Assert.Null(context.Result);
        else
            Assert.Equal(expectedStatus, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }
}
#endif
