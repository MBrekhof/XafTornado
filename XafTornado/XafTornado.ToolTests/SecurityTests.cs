using System.Text.Json.Nodes;
using Xunit;

namespace XafTornado.ToolTests;

// SEC-001: tools read and write through the calling user's secured object space and say so.
// Users and roles: see AppFixture (Admin, User with the seeded Default role, reader, germany, nophone).

[Collection(nameof(AppCollection))]
public class SecurityTests(AppFixture app)
{
    [Fact]
    public async Task DefaultRole_CannotReadCreateOrUpdate_AndIsToldSo()
    {
        var read = await app.InvokeAs("User", "query_entity", new { entityName = "Customer" });
        Assert.Equal("permission denied", read["error"]!.GetValue<string>());
        Assert.Equal("Customer", read["entity"]!.GetValue<string>());
        Assert.Equal("read", read["operation"]!.GetValue<string>());
        Assert.Null(read["records"]);   // not an empty list dressed up as "no customers"

        var create = await app.InvokeAs("User", "create_entity", new { entityName = "Category", properties = "Name=Nope" });
        Assert.Equal("permission denied", create["error"]!.GetValue<string>());
        Assert.Equal("create", create["operation"]!.GetValue<string>());

        var update = await app.InvokeAs("User", "update_entity", new { entityName = "Customer", identifier = "Alfreds Futterkiste", properties = "City=Nowhere" });
        Assert.Equal("permission denied", update["error"]!.GetValue<string>());

        var open = await app.InvokeAs("User", "navigate_to_detail", new { entityName = "Customer", identifier = "Alfreds Futterkiste" });
        Assert.Equal("permission denied", open["error"]!.GetValue<string>());

        // Nothing leaked through: Admin still sees the seed data untouched.
        var admin = await app.Invoke("query_entity", new { entityName = "Customer", filter = "City=Nowhere" });
        Assert.Equal(0, admin["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButUpdateIsDenied_NotSilentlyDropped()
    {
        var read = await app.InvokeAs(AppFixture.Reader, "query_entity", new { entityName = "Customer", top = 1000 });
        Assert.Equal(20, read["count"]!.GetValue<int>());

        var update = await app.InvokeAs(AppFixture.Reader, "update_entity", new { entityName = "Customer", identifier = "Alfreds Futterkiste", properties = "City=Nowhere" });
        Assert.Equal("permission denied", update["error"]!.GetValue<string>());
        Assert.Equal("write", update["operation"]!.GetValue<string>());
        Assert.Null(update["updated"]);

        var reread = await app.Invoke("query_entity", new { entityName = "Customer", filter = "CompanyName=Alfreds Futterkiste" });
        Assert.Equal("Berlin", reread["records"]![0]!["City"]!.GetValue<string>());
    }

    [Fact]
    public async Task RowRestrictedRole_SeesOnlyItsRows_AndCannotReachOthersById()
    {
        var read = await app.InvokeAs(AppFixture.Germany, "query_entity", new { entityName = "Customer", top = 1000 });
        Assert.Equal(3, read["count"]!.GetValue<int>());
        Assert.All(read["records"]!.AsArray(), c => Assert.Equal("Germany", c!["Country"]!.GetValue<string>()));

        var horn = (await app.Invoke("query_entity", new { entityName = "Customer", filter = "CompanyName=Around the Horn" }))["records"]![0]!["id"]!.GetValue<string>();
        var byId = await app.InvokeAs(AppFixture.Germany, "update_entity", new { entityName = "Customer", identifier = horn, properties = "City=Nowhere" });
        Assert.StartsWith("No Customer record found", byId["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task MemberRestrictedRole_GetsTheRecordWithoutTheHiddenMember()
    {
        var read = await app.InvokeAs(AppFixture.NoPhone, "query_entity", new { entityName = "Customer", filter = "CompanyName=Alfreds Futterkiste" });
        var record = read["records"]![0]!.AsObject();

        Assert.Equal("Alfreds Futterkiste", record["CompanyName"]!.GetValue<string>());
        Assert.False(record.ContainsKey("Phone"));   // not "" or null pretending to be the value

        var admin = await app.Invoke("query_entity", new { entityName = "Customer", filter = "CompanyName=Alfreds Futterkiste" });
        Assert.True(admin["records"]![0]!.AsObject().ContainsKey("Phone"));
    }

    [Fact]
    public async Task ReferenceToAnUnreadableType_IsDenied_NotNotFound()
    {
        // orders may create Orders and read Customers; Employee is explicitly denied: the reference
        // lookup must say "permission denied" for Employee, not "Nancy not found". (A type with no
        // permission at all gets XAF's implicit read for referenced objects, so CanRead is true and
        // the hidden rows read as "not found"; that is the secured space's own answer.)
        var before = (await app.Invoke("query_entity", new { entityName = "Order", top = 1000 }))["count"]!.GetValue<int>();

        var r = await app.InvokeAs(AppFixture.Orders, "create_entity", new { entityName = "Order", properties = "Customer=Alfreds;Employee=Nancy;OrderDate=2026-09-22" });
        Assert.Equal("permission denied", r["error"]!.GetValue<string>());
        Assert.Equal("Employee", r["entity"]!.GetValue<string>());
        Assert.Equal("read", r["operation"]!.GetValue<string>());

        var after = (await app.Invoke("query_entity", new { entityName = "Order", top = 1000 }))["count"]!.GetValue<int>();
        Assert.Equal(before, after);

        // The same user may set a readable reference: the tool works, it is not the user that is broken.
        var ok = await app.InvokeAs(AppFixture.Orders, "create_entity", new { entityName = "Order", properties = "Customer=Alfreds;OrderDate=2026-09-22" });
        Assert.True(ok["created"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Admin_IsUnchanged()
    {
        var r = await app.Invoke("query_entity", new { entityName = "Customer", top = 1000 });
        Assert.Equal(20, r["count"]!.GetValue<int>());
        Assert.Null(r["error"]);
    }

    // AI-011: get_current_user_permissions answers before the model tries and fails.

    private static (bool read, bool create, bool write, bool delete) Flags(JsonNode entity) =>
        (entity["read"]!.GetValue<bool>(), entity["create"]!.GetValue<bool>(), entity["write"]!.GetValue<bool>(), entity["delete"]!.GetValue<bool>());

    [Fact]
    public async Task Permissions_Admin_AllTrue()
    {
        var r = await app.Invoke("get_current_user_permissions");
        Assert.Equal("Admin", r["user"]!.GetValue<string>());
        Assert.Contains("Administrators", r["roles"]!.AsArray().Select(x => x!.GetValue<string>()));
        var entities = r["entities"]!.AsArray();
        Assert.NotEmpty(entities);
        Assert.All(entities, e => Assert.Equal((true, true, true, true), Flags(e!)));
    }

    [Fact]
    public async Task Permissions_DefaultRole_AllFalse()
    {
        var r = await app.InvokeAs("User", "get_current_user_permissions");
        Assert.Equal("User", r["user"]!.GetValue<string>());
        Assert.Equal(["Default"], r["roles"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var entities = r["entities"]!.AsArray();
        Assert.NotEmpty(entities);
        Assert.All(entities, e => Assert.Equal((false, false, false, false), Flags(e!)));
        Assert.NotNull(r["note"]);
    }

    [Fact]
    public async Task Permissions_Reader_ReadOnlyOnCustomer()
    {
        var r = await app.InvokeAs(AppFixture.Reader, "get_current_user_permissions", new { entityName = "customer" });
        Assert.Equal(["Reader"], r["roles"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var entity = Assert.Single(r["entities"]!.AsArray());
        Assert.Equal("Customer", entity!["name"]!.GetValue<string>());
        Assert.Equal((true, false, false, false), Flags(entity));
    }

    [Fact]
    public async Task Permissions_Orders_ExplicitDenyShowsFalse()
    {
        var r = await app.InvokeAs(AppFixture.Orders, "get_current_user_permissions");
        var byName = r["entities"]!.AsArray().ToDictionary(e => e!["name"]!.GetValue<string>(), e => e!);
        // Employee: Read explicitly denied. Write is true on Employee and Customer because the role's
        // CRUD on Order gives an automatic association permission on their Orders collections
        // (AssociationPermissionsMode.Auto) — XAF's own type-level answer, which the tool reports as
        // is and explains in `note`. Reader (no Order access) shows Customer write false above.
        Assert.Equal((false, false, true, false), Flags(byName["Employee"]));
        Assert.Equal((true, true, true, true), Flags(byName["Order"]));
        Assert.Equal((true, false, true, false), Flags(byName["Customer"]));
    }

    [Fact]
    public async Task Permissions_UnknownEntity_ReturnsError()
    {
        var r = await app.Invoke("get_current_user_permissions", new { entityName = "Nope" });
        Assert.NotNull(r["error"]);
        Assert.NotNull(r["availableEntities"]);
        Assert.Null(r["entities"]);
    }
}
