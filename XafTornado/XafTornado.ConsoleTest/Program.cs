using System.ComponentModel;
using System.Text;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

// ── Logging ───────────────────────────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("ConsoleTest");

// ── Simple tools (no XAF dependency) ──────────────────────────────────
var toolClass = new FakeTools();
var tools = new List<AIFunction>
{
    AIFunctionFactory.Create(toolClass.QueryOrders, "query_orders"),
    AIFunctionFactory.Create(toolClass.InvoiceAging, "invoice_aging"),
    AIFunctionFactory.Create(toolClass.LowStockProducts, "low_stock_products"),
    AIFunctionFactory.Create(toolClass.EmployeeOrderStats, "employee_order_stats"),
    AIFunctionFactory.Create(toolClass.EmployeeTerritories, "employee_territories"),
    AIFunctionFactory.Create(toolClass.CreateOrder, "create_order"),
};

// System message matching Blazor app
const string SystemPrompt = @"
You are a helpful business assistant for a Northwind-style order management application.
The database contains these entities:

- **Customer** (CompanyName, ContactName, Phone, Email, City, Country) -> has many Orders
- **Order** (OrderDate, RequiredDate, ShippedDate, Freight, ShipAddress, ShipCity, ShipCountry, Status) -> belongs to Customer, Employee, Shipper, Invoice; has many OrderItems
  - OrderStatus values: New, Processing, Shipped, Delivered, Cancelled
- **OrderItem** (UnitPrice, Quantity, Discount) -> belongs to Order and Product
- **Product** (Name, UnitPrice, UnitsInStock, Discontinued) -> belongs to Category, Supplier
- **Category** (Name, Description) -> has many Products
- **Supplier** (CompanyName, ContactName, Phone, Email, City, Country) -> has many Products
- **Employee** (FirstName, LastName, Title, HireDate, Email, Phone) -> has many Orders, Territories, DirectReports; may report to another Employee
- **EmployeeTerritory** -> links Employee to Territory
- **Territory** (Name) -> belongs to Region
- **Region** (Name)
- **Shipper** (CompanyName, Phone) -> has many Orders
- **Invoice** (InvoiceNumber, InvoiceDate, DueDate, Status, computed TotalAmount) -> has many Orders
  - InvoiceStatus values: Draft, Sent, Paid, Overdue, Cancelled

When answering:
- Use Markdown formatting for readability (tables, bold, lists).
- Be concise but thorough.
";

// ── AI Client ─────────────────────────────────────────────────────────
Console.WriteLine("=== AI SDK Console Test ===\n");

var client = new CopilotClient(new CopilotClientOptions
{
    UseLoggedInUser = true,
    Logger = loggerFactory.CreateLogger<CopilotClient>()
});

Console.WriteLine("[1] Starting client...");
await client.StartAsync();
Console.WriteLine("[1] Client started.\n");

// ── Test 1: Skip to save time ────────────────────────────────────────
// Console.WriteLine("=== Test 1: Simple prompt (no tools) ===");
// await RunTest(client, "What is 2+2? Reply with just the number.", tools: null, "gpt-4o");

// ── Test 2: gpt-5 with invoice aging (to test if gpt-5 is the problem) ──
Console.WriteLine("\n=== Test 2: Invoice aging tool - gpt-5 ===");
await RunTest(client, "Give me an aging summary of overdue invoices grouped by customer, including totals and recommendations", tools, "gpt-5");

// ── Test 3: gpt-4o with invoice aging (known to work) ───────────────────
Console.WriteLine("\n=== Test 3: Invoice aging tool - gpt-4o ===");
await RunTest(client, "Give me an aging summary of overdue invoices grouped by customer, including totals and recommendations", tools, "gpt-4o");

// ── Cleanup ──────────────────────────────────────────────────────────
Console.WriteLine("\n[Done] Stopping client...");
await client.StopAsync();
await client.DisposeAsync();
Console.WriteLine("[Done] Finished.");

// ── Test runner ──────────────────────────────────────────────────────
async Task RunTest(CopilotClient client, string prompt, List<AIFunction>? tools, string model = "gpt-4o")
{
    var config = new SessionConfig
    {
        Model = model,
        Streaming = true,
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Append,
            Content = SystemPrompt
        }
    };
    if (tools is { Count: > 0 })
        config.Tools = tools;

    Console.WriteLine($"  Creating session (model={config.Model}, streaming={config.Streaming}, tools={tools?.Count ?? 0})...");
    await using var session = await client.CreateSessionAsync(config);

    var buffer = new StringBuilder();
    var events = new List<string>();
    string? lastError = null;
    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var sub = session.On(evt =>
    {
        var name = evt.GetType().Name;
        events.Add(name);
        Console.WriteLine($"  [Event] {name}");

        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                var chunk = delta.Data.DeltaContent;
                Console.Write(chunk);    // stream to console live
                buffer.Append(chunk);
                break;
            case AssistantMessageEvent msg:
                Console.WriteLine($"  [AssistantMessage] Content length: {msg.Data?.Content?.Length ?? 0}");
                break;
            case SessionErrorEvent err:
                lastError = err.Data?.Message ?? "(null)";
                Console.WriteLine($"  [SessionError] {lastError}");
                idleTcs.TrySetResult(false);
                break;
            case SessionIdleEvent:
                Console.WriteLine($"  [SessionIdle] buffer={buffer.Length} chars");
                idleTcs.TrySetResult(true);
                break;
            case ToolExecutionStartEvent:
                Console.WriteLine("  [ToolStart]");
                break;
            case ToolExecutionCompleteEvent:
                Console.WriteLine("  [ToolComplete]");
                break;
        }
    });

    try
    {
        Console.WriteLine($"  Sending: \"{prompt}\"");
        
        // Fire-and-forget the send — response comes via events
        var sendTask = session.SendAsync(new MessageOptions { Prompt = prompt });

        // Wait for SessionIdleEvent or error (with 2-min timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // Wait for EITHER: idle/error event OR sendTask completion
        var idleTask = idleTcs.Task;
        
        try
        {
            // First, wait for sendTask to return the message ID
            var messageId = await sendTask.WaitAsync(cts.Token);
            Console.WriteLine($"\n  SendAsync returned: {messageId}");

            // Then wait for idle event (response is still streaming)
            Console.WriteLine("  Waiting for SessionIdleEvent...");
            await idleTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  TIMED OUT (2 min)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  THREW: {ex.GetType().Name}: {ex.Message}");
        }

        // Results
        Console.WriteLine($"\n  Events fired: {string.Join(", ", events.Distinct())}");
        Console.WriteLine($"  Total events: {events.Count}");
        Console.WriteLine($"  Buffer length: {buffer.Length}");
        if (buffer.Length > 0)
        {
            Console.WriteLine("  --- Response ---");
            Console.WriteLine($"  {buffer}");
            Console.WriteLine("  --- End ---");
        }
        if (lastError != null)
            Console.WriteLine($"  Last error: {lastError}");
    }
    finally
    {
        sub.Dispose();
    }
}

// ── Fake tools matching Blazor app's AIToolsProvider ────────────
class FakeTools
{
    [Description("Search orders by customer name and/or status. Returns up to 25 orders with line items.")]
    public string QueryOrders(
        [Description("Customer company name to search for (partial match). Omit for all customers.")] string customerName = "",
        [Description("Order status filter: New, Processing, Shipped, Delivered, Cancelled. Omit for all.")] string status = "")
    {
        Console.WriteLine($"  >> [Tool:query_orders] Called with customerName={customerName}, status={status}");
        return "Found 3 order(s):\n[2025-12-01] Processing | Around the Horn | Employee: Nancy Davolio | Shipper: Speedy Express | Freight: 25.50 | Ship: London, UK | Items: Chai x10 @18.00; Chang x5 @19.00";
    }

    [Description("Get invoice aging summary grouped by customer. Shows overdue invoices by default.")]
    public string InvoiceAging(
        [Description("Invoice status: Draft, Sent, Paid, Overdue, Cancelled. Defaults to Overdue.")] string status = "Overdue")
    {
        Console.WriteLine($"  >> [Tool:invoice_aging] Called with status={status}");
        // EXACT output from the real Blazor database tool (737 chars)
        return "Invoice Aging (Overdue) \u2014 7 customer(s), Grand Total: 53869.81\n" +
               "Cactus Comidas: 1 invoice(s), Total: 11766.09, Oldest Due: 2024-06-27, Invoices: INV-0011\n" +
               "Antonio Moreno Taquer\u00eda: 2 invoice(s), Total: 11744.25, Oldest Due: 2024-04-18, Invoices: INV-0002, INV-0011\n" +
               "Centro comercial Moctezuma: 1 invoice(s), Total: 10236.20, Oldest Due: 2024-07-03, Invoices: INV-0001\n" +
               "Ernst Handel: 1 invoice(s), Total: 8109.57, Oldest Due: 2024-07-03, Invoices: INV-0001\n" +
               "Consolidated Holdings: 1 invoice(s), Total: 7074.30, Oldest Due: 2024-06-27, Invoices: INV-0011\n" +
               "Chop-suey Chinese: 1 invoice(s), Total: 2620.86, Oldest Due: 2024-04-18, Invoices: INV-0002\n" +
               "Ana Trujillo Emparedados: 1 invoice(s), Total: 2318.53, Oldest Due: 2024-04-18, Invoices: INV-0002";
    }

    [Description("Find non-discontinued products with stock below a given threshold. Includes supplier info.")]
    public string LowStockProducts(
        [Description("Stock threshold. Products below this level are returned. Default is 20.")] int threshold = 20)
    {
        Console.WriteLine($"  >> [Tool:low_stock_products] Called with threshold={threshold}");
        return $"Low stock (below {threshold}): 3 product(s)\nChai | Stock: 5 | Price: 18.00 | Category: Beverages | Supplier: Exotic Liquids (Charlotte Cooper, 555-0100)";
    }

    [Description("Get order count and total freight per employee, optionally filtered by date range.")]
    public string EmployeeOrderStats(
        [Description("Start date in yyyy-MM-dd format. Omit for all time.")] string fromDate = "",
        [Description("End date in yyyy-MM-dd format. Omit for all time.")] string toDate = "")
    {
        Console.WriteLine($"  >> [Tool:employee_order_stats] Called");
        return "Employee Order Stats (from: all, to: all)\nNancy Davolio (Sales Representative): 12 orders, Freight: 350.00 total / 29.17 avg, Last: 2025-12-01";
    }

    [Description("List all employees with their territory counts and territory/region details.")]
    public string EmployeeTerritories()
    {
        Console.WriteLine($"  >> [Tool:employee_territories] Called");
        return "Employee Territories:\nNancy Davolio: 3 — New York (East), Philadelphia (East), Boston (East)";
    }

    [Description("Create a new order for a customer with a product and shipper.")]
    public string CreateOrder(
        [Description("Customer company name (must match an existing customer).")] string customerName,
        [Description("Product name (must match an existing product).")] string productName,
        [Description("Quantity to order. Must be greater than 0.")] int quantity,
        [Description("Shipper company name (must match an existing shipper).")] string shipperName)
    {
        Console.WriteLine($"  >> [Tool:create_order] Called with customer={customerName}, product={productName}, qty={quantity}, shipper={shipperName}");
        return $"Order created! Date: 2025-12-10, Customer: {customerName}, Product: {productName} x{quantity} @18.00 = {18 * quantity}.00, Shipper: {shipperName}, Status: New";
    }
}
