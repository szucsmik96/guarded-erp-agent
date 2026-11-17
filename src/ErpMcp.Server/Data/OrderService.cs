using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ErpMcp.Server.Data;

public sealed record OrderLineInput(
    [property: Description("Item code, e.g. A00001.")] string ItemCode,
    [property: Description("Quantity to order. Must be greater than zero.")] int Quantity);

public sealed record OrderIssue(string Code, string Message);

public sealed record PricedLine(
    string ItemCode, string ItemName, int Quantity, int Available, decimal UnitPrice, decimal LineTotal);

public sealed record CreditSummary(decimal CreditLimit, decimal Balance, decimal OpenOrders, decimal Available);

public sealed record OrderCheck(
    bool Ok,
    string CardCode,
    string? CardName,
    IReadOnlyList<PricedLine> Lines,
    decimal DocTotal,
    CreditSummary? Credit,
    IReadOnlyList<OrderIssue> Issues);

/// <summary>Replayed = true when the requestId was seen before and the existing order is returned.</summary>
public sealed record OrderResult(bool Created, long? DocNum, OrderCheck Check, bool Replayed = false);

/// <summary>
/// Business rules live on the server, not in the prompt. The model can be wrong or manipulated;
/// these checks run no matter what it asks for.
/// </summary>
public sealed class OrderService(ErpDb db)
{
    public OrderCheck Validate(string cardCode, IReadOnlyList<OrderLineInput> lines)
    {
        using var c = db.Open();
        return Check(c, null, cardCode, lines);
    }

    public OrderResult Create(string cardCode, IReadOnlyList<OrderLineInput> lines, string? requestId = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        // Idempotency: the same requestId always maps to the same order, so a retried call is applied once.
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            var existing = Sql.Scalar(c, tx, "SELECT DocNum FROM Orders WHERE RequestId = $rid", new { rid = requestId });
            if (existing is not null)
            {
                var existingDoc = Convert.ToInt64(existing);
                var summary = LoadOrder(c, tx, existingDoc);
                tx.Commit();
                return new OrderResult(true, existingDoc, summary, Replayed: true);
            }
        }

        // Re-validate inside the transaction: stock or credit may have changed since validate_sales_order ran.
        var check = Check(c, tx, cardCode, lines);
        if (!check.Ok)
        {
            Audit(c, tx, "create_sales_order.rejected", new { cardCode, lines, check.Issues });
            tx.Commit();
            return new OrderResult(false, null, check);
        }

        Sql.Execute(c, tx,
            "INSERT INTO Orders (CardCode, Status, DocTotal, CreatedAt, RequestId) VALUES ($card, 'Open', $total, $at, $rid)",
            new { card = check.CardCode, total = check.DocTotal, at = DateTime.UtcNow.ToString("O"), rid = string.IsNullOrWhiteSpace(requestId) ? null : requestId });
        var docNum = Convert.ToInt64(Sql.Scalar(c, tx, "SELECT last_insert_rowid()"));

        var lineNum = 0;
        foreach (var line in check.Lines)
        {
            Sql.Execute(c, tx,
                "INSERT INTO OrderLines (DocNum, LineNum, ItemCode, Quantity, Price) VALUES ($doc, $line, $item, $qty, $price)",
                new { doc = docNum, line = lineNum++, item = line.ItemCode, qty = line.Quantity, price = line.UnitPrice });
            Sql.Execute(c, tx,
                "UPDATE Items SET Committed = Committed + $qty WHERE ItemCode = $item",
                new { qty = line.Quantity, item = line.ItemCode });
        }

        Audit(c, tx, "create_sales_order.created", new { docNum, requestId, check.CardCode, check.DocTotal, check.Lines });
        tx.Commit();
        return new OrderResult(true, docNum, check);
    }

    private static OrderCheck LoadOrder(SqliteConnection c, SqliteTransaction tx, long docNum)
    {
        var card = Sql.Query(c, tx,
            "SELECT o.CardCode, cu.CardName FROM Orders o JOIN Customers cu ON cu.CardCode = o.CardCode WHERE o.DocNum = $doc",
            r => (Code: r.GetString(0), Name: r.GetString(1)), new { doc = docNum }).Single();
        var lines = Sql.Query(c, tx,
            """
            SELECT l.ItemCode, i.ItemName, l.Quantity, i.OnHand - i.Committed, l.Price
            FROM OrderLines l JOIN Items i ON i.ItemCode = l.ItemCode
            WHERE l.DocNum = $doc ORDER BY l.LineNum
            """,
            r => new PricedLine(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetDecimal(4), r.GetDecimal(4) * r.GetInt32(2)),
            new { doc = docNum });
        return new OrderCheck(true, card.Code, card.Name, lines, lines.Sum(l => l.LineTotal), null, []);
    }

    internal static CreditSummary CreditFor(decimal limit, decimal balance, decimal openOrders) =>
        new(limit, balance, openOrders, limit - balance - openOrders);

    private static OrderCheck Check(
        SqliteConnection c, SqliteTransaction? tx, string cardCode, IReadOnlyList<OrderLineInput>? lines)
    {
        var issues = new List<OrderIssue>();
        lines ??= [];

        var customer = Sql.Query(c, tx,
            """
            SELECT c.CardCode, c.CardName, c.CreditLimit, c.Balance,
                   COALESCE((SELECT SUM(o.DocTotal) FROM Orders o
                             WHERE o.CardCode = c.CardCode AND o.Status = 'Open'), 0)
            FROM Customers c
            WHERE c.CardCode = $code COLLATE NOCASE
            """,
            r => new CustomerRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)),
            new { code = cardCode.Trim() }).FirstOrDefault();

        CreditSummary? credit = null;
        if (customer is null)
        {
            issues.Add(new("CUSTOMER_NOT_FOUND", $"No customer with code '{cardCode}'. Use find_customers first."));
        }
        else
        {
            credit = CreditFor(customer.CreditLimit, customer.Balance, customer.OpenOrders);
        }

        if (lines.Count == 0)
        {
            issues.Add(new("NO_LINES", "An order needs at least one line."));
        }

        var priced = new List<PricedLine>();
        foreach (var group in lines.GroupBy(l => l.ItemCode.Trim().ToUpperInvariant()))
        {
            if (group.Any(l => l.Quantity <= 0))
            {
                issues.Add(new("INVALID_QUANTITY", $"Quantity for {group.Key} must be greater than zero."));
                continue;
            }

            var item = Sql.Query(c, tx,
                "SELECT ItemCode, ItemName, Price, OnHand - Committed FROM Items WHERE ItemCode = $code COLLATE NOCASE",
                r => new ItemRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3)),
                new { code = group.Key }).FirstOrDefault();

            if (item is null)
            {
                issues.Add(new("ITEM_NOT_FOUND", $"No item with code '{group.Key}'. Use search_items first."));
                continue;
            }

            var qty = group.Sum(l => l.Quantity);
            if (qty > item.Available)
            {
                issues.Add(new("INSUFFICIENT_STOCK",
                    $"{item.ItemCode} ({item.ItemName}): requested {qty}, available {item.Available}."));
            }

            priced.Add(new PricedLine(item.ItemCode, item.ItemName, qty, item.Available, item.Price, item.Price * qty));
        }

        var total = priced.Sum(p => p.LineTotal);
        if (credit is not null && total > credit.Available)
        {
            issues.Add(new("CREDIT_LIMIT_EXCEEDED", string.Create(CultureInfo.InvariantCulture,
                $"Order total {total:0.00} exceeds available credit {credit.Available:0.00}.")));
        }

        return new OrderCheck(
            issues.Count == 0, customer?.CardCode ?? cardCode, customer?.CardName, priced, total, credit, issues);
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, string action, object payload) =>
        Sql.Execute(c, tx,
            "INSERT INTO AuditLog (At, Action, Payload) VALUES ($at, $action, $payload)",
            new { at = DateTime.UtcNow.ToString("O"), action, payload = JsonSerializer.Serialize(payload) });

    private sealed record CustomerRow(
        string CardCode, string CardName, decimal CreditLimit, decimal Balance, decimal OpenOrders);

    private sealed record ItemRow(string ItemCode, string ItemName, decimal Price, int Available);
}
