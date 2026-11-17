using System.ComponentModel;
using System.Text.Json;
using ErpMcp.Server.Data;
using ModelContextProtocol.Server;

namespace ErpMcp.Server.Tools;

/// <summary>
/// The MCP surface. Every tool declares MCP annotations; the agent uses ReadOnly to decide
/// what may run without a human. Anything not marked read-only needs approval (fail closed).
/// </summary>
[McpServerToolType]
public static class ErpTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "search_items", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search the item master by item code or name fragment. Returns up to 10 items with unit price " +
                 "and available quantity (on hand minus committed to open orders).")]
    public static string SearchItems(
        ErpDb db,
        [Description("Part of an item code or name, e.g. 'chair' or 'A0000'.")] string query)
    {
        using var c = db.Open();
        var rows = Sql.Query(c, null,
            """
            SELECT ItemCode, ItemName, Price, OnHand, Committed, OnHand - Committed
            FROM Items
            WHERE ItemCode LIKE $q OR ItemName LIKE $q
            ORDER BY ItemCode
            LIMIT 10
            """,
            r => new
            {
                itemCode = r.GetString(0),
                itemName = r.GetString(1),
                unitPrice = r.GetDecimal(2),
                onHand = r.GetInt32(3),
                committed = r.GetInt32(4),
                available = r.GetInt32(5),
            },
            new { q = $"%{query.Trim()}%" });

        return JsonSerializer.Serialize(new { count = rows.Count, items = rows }, Json);
    }

    [McpServerTool(Name = "find_customers", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Find customers by code or name fragment. Returns up to 10 customers with credit limit, " +
                 "current balance, open order exposure and available credit.")]
    public static string FindCustomers(
        ErpDb db,
        [Description("Part of a customer code or name, e.g. 'nordic' or 'C400'.")] string query)
    {
        using var c = db.Open();
        var rows = Sql.Query(c, null,
            """
            SELECT c.CardCode, c.CardName, c.CreditLimit, c.Balance,
                   COALESCE((SELECT SUM(o.DocTotal) FROM Orders o
                             WHERE o.CardCode = c.CardCode AND o.Status = 'Open'), 0)
            FROM Customers c
            WHERE c.CardCode LIKE $q OR c.CardName LIKE $q
            ORDER BY c.CardName
            LIMIT 10
            """,
            r => new
            {
                cardCode = r.GetString(0),
                cardName = r.GetString(1),
                credit = OrderService.CreditFor(r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)),
            },
            new { q = $"%{query.Trim()}%" });

        return JsonSerializer.Serialize(new { count = rows.Count, customers = rows }, Json);
    }

    [McpServerTool(Name = "list_open_orders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List a customer's open sales orders with their lines.")]
    public static string ListOpenOrders(
        ErpDb db,
        [Description("Exact customer code, e.g. C30000.")] string cardCode)
    {
        using var c = db.Open();
        var lines = Sql.Query(c, null,
            """
            SELECT o.DocNum, o.DocTotal, o.CreatedAt, l.ItemCode, i.ItemName, l.Quantity, l.Price
            FROM Orders o
            JOIN OrderLines l ON l.DocNum = o.DocNum
            JOIN Items i ON i.ItemCode = l.ItemCode
            WHERE o.CardCode = $code COLLATE NOCASE AND o.Status = 'Open'
            ORDER BY o.DocNum, l.LineNum
            """,
            r => new
            {
                DocNum = r.GetInt64(0),
                DocTotal = r.GetDecimal(1),
                CreatedAt = r.GetString(2),
                Line = new { itemCode = r.GetString(3), itemName = r.GetString(4), quantity = r.GetInt32(5), unitPrice = r.GetDecimal(6) },
            },
            new { code = cardCode.Trim() });

        var orders = lines
            .GroupBy(l => (l.DocNum, l.DocTotal, l.CreatedAt))
            .Select(g => new { docNum = g.Key.DocNum, docTotal = g.Key.DocTotal, createdAt = g.Key.CreatedAt, lines = g.Select(x => x.Line) })
            .ToList();

        return JsonSerializer.Serialize(new { cardCode, count = orders.Count, orders }, Json);
    }

    [McpServerTool(Name = "validate_sales_order", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Dry run: price a sales order and check it against stock and the customer's credit limit, " +
                 "without saving anything. Always call this before create_sales_order and show the result to the user.")]
    public static string ValidateSalesOrder(
        OrderService orders,
        [Description("Exact customer code, e.g. C40000.")] string cardCode,
        [Description("Order lines.")] OrderLineInput[] lines)
    {
        var check = orders.Validate(cardCode, lines);
        return JsonSerializer.Serialize(check, Json);
    }

    [McpServerTool(Name = "create_sales_order", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a sales order and commit stock. This writes to the ERP. The server re-runs every " +
                 "validation; if any rule fails, nothing is saved and the issues are returned.")]
    public static string CreateSalesOrder(
        OrderService orders,
        [Description("Exact customer code, e.g. C40000.")] string cardCode,
        [Description("Order lines.")] OrderLineInput[] lines)
    {
        var result = orders.Create(cardCode, lines);
        return JsonSerializer.Serialize(result, Json);
    }
}
