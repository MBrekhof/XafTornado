using System.Text.Json.Nodes;
using Xunit;

namespace XafTornado.ToolTests;

// All classes share one fixture and run sequentially (same collection), so write tests
// don't race each other. Seed data is Random(12345) — counts and names are deterministic.

[Collection(nameof(AppCollection))]
public class SchemaToolTests(AppFixture app)
{
    [Fact]
    public async Task ListEntities_ReturnsAllVisibleEntities_WithRelationships()
    {
        var r = await app.Invoke("list_entities");

        var entities = r["entities"]!.AsArray();
        Assert.Equal(13, entities.Count);

        var order = entities.Single(e => e!["name"]!.GetValue<string>() == "Order")!;
        Assert.Contains("OrderDate", order["properties"]!.AsArray().Select(p => p!.GetValue<string>()));
        var customerRel = order["relationships"]!.AsArray().Single(x => x!["property"]!.GetValue<string>() == "Customer")!;
        Assert.Equal("belongsTo", customerRel["kind"]!.GetValue<string>());
        Assert.Equal("Customer", customerRel["target"]!.GetValue<string>());
        Assert.Contains("New", order["enums"]!["Status"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task DescribeEntity_ReturnsTypedProperties_AndEnumValues()
    {
        var r = await app.Invoke("describe_entity", new { entityName = "Order" });

        Assert.Equal("Order", r["name"]!.GetValue<string>());
        var props = r["properties"]!.AsArray();
        var orderDate = props.Single(p => p!["name"]!.GetValue<string>() == "OrderDate")!;
        Assert.Equal("DateTime", orderDate["type"]!.GetValue<string>());
        Assert.True(orderDate["required"]!.GetValue<bool>());
        var status = props.Single(p => p!["name"]!.GetValue<string>() == "Status")!;
        Assert.Contains("Shipped", status["values"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task DescribeEntity_UnknownEntity_ReturnsErrorWithAvailableEntities()
    {
        var r = await app.Invoke("describe_entity", new { entityName = "Foo" });

        Assert.Equal("Entity 'Foo' not found.", r["error"]!.GetValue<string>());
        Assert.Contains("Order", r["availableEntities"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public async Task ToolSet_HasAllTwelveTools()
    {
        var names = app.Tools.Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
        [
            "clear_active_list_filter", "close_active_view", "create_entity", "describe_entity",
            "filter_active_list", "get_active_view", "list_entities", "navigate_to_detail",
            "navigate_to_list", "query_entity", "save_active_view", "update_entity",
        ], names);
    }
}

[Collection(nameof(AppCollection))]
public class QueryEntityTests(AppFixture app)
{
    [Fact]
    public async Task Query_AllOrders_ReturnsSeedCount_WithIdsAndReferences()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Order", top = 1000 });

        Assert.Equal("Order", r["entity"]!.GetValue<string>());
        Assert.Equal(50, r["count"]!.GetValue<int>());
        Assert.Null(r["truncated"]);

        var first = r["records"]!.AsArray()[0]!;
        Assert.True(Guid.TryParse(first["id"]!.GetValue<string>(), out _), "records carry the XAF key as id");
        Assert.IsType<string>(first["Customer"]!.GetValue<string>());   // to-one reference → display text
        Assert.True(first["Freight"]!.GetValue<decimal>() >= 0);         // raw numeric, not "69.87" text
    }

    [Fact]
    public async Task Query_Top_TruncatesAndFlagsIt()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Order", top = 5 });

        Assert.Equal(5, r["count"]!.GetValue<int>());
        Assert.Equal(5, r["records"]!.AsArray().Count);
        Assert.True(r["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Query_StringFilter_IsCaseInsensitiveContains()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Customer", filter = "Country=germany" });

        Assert.Equal(3, r["count"]!.GetValue<int>());
        Assert.All(r["records"]!.AsArray(), c => Assert.Equal("Germany", c!["Country"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Query_EnumFilter_MatchesExactly()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Order", filter = "Status=New", top = 1000 });

        Assert.True(r["count"]!.GetValue<int>() > 0);
        Assert.All(r["records"]!.AsArray(), o => Assert.Equal("New", o!["Status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Query_ReferenceFilter_MatchesDisplayText()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Order", filter = "Customer=Du monde", top = 1000 });

        Assert.Equal(6, r["count"]!.GetValue<int>());
        Assert.All(r["records"]!.AsArray(), o => Assert.Equal("Du monde entier", o!["Customer"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Query_CombinedFilters_AreAnded()
    {
        var all = await app.Invoke("query_entity", new { entityName = "Order", filter = "Customer=Du monde", top = 1000 });
        var newOnly = await app.Invoke("query_entity", new { entityName = "Order", filter = "Customer=Du monde;Status=New", top = 1000 });

        Assert.True(newOnly["count"]!.GetValue<int>() < all["count"]!.GetValue<int>());
        Assert.All(newOnly["records"]!.AsArray(), o => Assert.Equal("New", o!["Status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Query_NoMatch_ReturnsEmptyRecords_NotAnError()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Customer", filter = "Country=Atlantis" });

        Assert.Null(r["error"]);
        Assert.Equal(0, r["count"]!.GetValue<int>());
        Assert.Empty(r["records"]!.AsArray());
    }

    [Fact]
    public async Task Query_UnknownProperty_ReturnsErrorWithAvailableProperties()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Customer", filter = "Colour=red" });

        Assert.Equal("Property 'Colour' not found on Customer.", r["error"]!.GetValue<string>());
        Assert.Contains("Country", r["availableProperties"]!.AsArray().Select(p => p!.GetValue<string>()));
    }

    [Fact]
    public async Task Query_UnconvertibleFilterValue_ReturnsError()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Order", filter = "Freight=lots" });

        Assert.StartsWith("Cannot convert filter value 'lots'", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_UnknownEntity_ReturnsError()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Nope" });

        Assert.Equal("Entity 'Nope' not found.", r["error"]!.GetValue<string>());
    }
}

[Collection(nameof(AppCollection))]
public class MutationToolTests(AppFixture app)
{
    [Fact]
    public async Task Create_ThenUpdateById_RoundTrips()
    {
        var created = await app.Invoke("create_entity", new
        {
            entityName = "Category",
            properties = "Name=Test Widgets;Description=made by ToolTests",
        });

        Assert.True(created["created"]!.GetValue<bool>());
        var id = created["id"]!.GetValue<string>();
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("Test Widgets", created["values"]!["Name"]!.GetValue<string>());

        var found = await app.Invoke("query_entity", new { entityName = "Category", filter = "Name=Test Widgets" });
        Assert.Equal(1, found["count"]!.GetValue<int>());
        Assert.Equal(id, found["records"]![0]!["id"]!.GetValue<string>());

        var updated = await app.Invoke("update_entity", new
        {
            entityName = "Category",
            identifier = id,
            properties = "Description=updated by id",
        });

        Assert.True(updated["updated"]!.GetValue<bool>());
        Assert.Equal(id, updated["id"]!.GetValue<string>());
        Assert.Equal("Test Widgets", updated["display"]!.GetValue<string>());
        Assert.Equal("made by ToolTests", updated["changes"]!["Description"]!["from"]!.GetValue<string>());
        Assert.Equal("updated by id", updated["changes"]!["Description"]!["to"]!.GetValue<string>());

        var reread = await app.Invoke("query_entity", new { entityName = "Category", filter = "Name=Test Widgets" });
        Assert.Equal("updated by id", reread["records"]![0]!["Description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_WithReference_ResolvesByDisplayText_AndConvertsTypes()
    {
        var r = await app.Invoke("create_entity", new
        {
            entityName = "Product",
            properties = "Name=Test Tonic;UnitPrice=12.5;UnitsInStock=7;Category=bever",
        });

        Assert.True(r["created"]!.GetValue<bool>());
        Assert.Equal("Beverages", r["values"]!["Category"]!.GetValue<string>());
        Assert.Equal(12.5m, r["values"]!["UnitPrice"]!.GetValue<decimal>());
        Assert.Equal(7, r["values"]!["UnitsInStock"]!.GetValue<int>());
    }

    [Fact]
    public async Task Create_UnknownReference_ReturnsErrorWithAvailableTargets()
    {
        var r = await app.Invoke("create_entity", new { entityName = "Product", properties = "Name=X;Category=Nonexistent" });

        Assert.Equal("Category 'Nonexistent' not found.", r["error"]!.GetValue<string>());
        Assert.Contains("Beverages", r["available"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task Create_WithoutProperties_ReturnsErrorWithSettableNames()
    {
        var r = await app.Invoke("create_entity", new { entityName = "Order", properties = "" });

        Assert.Equal("Properties are required.", r["error"]!.GetValue<string>());
        var names = r["availableProperties"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Contains("OrderDate", names);
        Assert.Contains("Customer", names);   // to-one references are settable too
    }

    [Fact]
    public async Task Update_UnknownRecord_ReturnsError()
    {
        var r = await app.Invoke("update_entity", new { entityName = "Category", identifier = "no-such-category", properties = "Name=Y" });

        Assert.Equal("No Category record found matching 'no-such-category'.", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_BySearchTerm_UpdatesReference()
    {
        var before = await app.Invoke("query_entity", new { entityName = "Product", filter = "Name=Test Tonic" });
        if (before["count"]!.GetValue<int>() == 0)
            await app.Invoke("create_entity", new { entityName = "Product", properties = "Name=Test Tonic;UnitPrice=1;Category=Beverages" });

        var r = await app.Invoke("update_entity", new { entityName = "Product", identifier = "Test Tonic", properties = "Category=Dairy" });

        Assert.True(r["updated"]!.GetValue<bool>());
        Assert.Equal("Dairy", r["changes"]!["Category"]!["to"]!.GetValue<string>());
    }
}

[Collection(nameof(AppCollection))]
public class NavigationToolTests(AppFixture app)
{
    [Fact]
    public async Task NavigateToList_KnownEntity_Acknowledges()
    {
        var r = await app.Invoke("navigate_to_list", new { entityName = "Order" });

        Assert.Equal("navigate_to_list", r["action"]!.GetValue<string>());
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal("Order", r["entity"]!.GetValue<string>());
    }

    [Fact]
    public async Task NavigateToList_UnknownEntity_ReturnsError()
    {
        var r = await app.Invoke("navigate_to_list", new { entityName = "Nope" });

        Assert.Equal("Entity 'Nope' not found.", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task NavigateToDetail_RequiresIdentifier()
    {
        var r = await app.Invoke("navigate_to_detail", new { entityName = "Order", identifier = "" });

        Assert.StartsWith("An identifier", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActiveViewTools_WithoutAView_ReturnErrors()
    {
        var view = await app.Invoke("get_active_view");
        Assert.Equal("No active view context available.", view["error"]!.GetValue<string>());

        var filter = await app.Invoke("filter_active_list", new { criteria = "[Country] = 'USA'" });
        Assert.StartsWith("No active list view to filter", filter["error"]!.GetValue<string>());

        var clear = await app.Invoke("clear_active_list_filter");
        Assert.StartsWith("No active list view", clear["error"]!.GetValue<string>());
    }
}
