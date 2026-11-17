using ErpMcp.Server.Data;
using Microsoft.Data.Sqlite;

namespace ErpMcp.Server.Tests;

public sealed class OrderServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"erp-test-{Guid.NewGuid():N}.db");
    private readonly ErpDb _db;
    private readonly OrderService _orders;

    public OrderServiceTests()
    {
        _db = new ErpDb($"Data Source={_path}");
        _db.EnsureCreatedAndSeeded();
        _orders = new OrderService(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Valid_order_is_created_and_stock_is_committed()
    {
        var before = Committed("A00002");

        var result = _orders.Create("C40000", [new("A00002", 2)]);

        Assert.True(result.Created);
        Assert.NotNull(result.DocNum);
        Assert.Equal(898m, result.Check.DocTotal);
        Assert.Equal(before + 2, Committed("A00002"));
    }

    [Fact]
    public void Out_of_stock_item_is_rejected()
    {
        var check = _orders.Validate("C40000", [new("A00007", 1)]);

        Assert.False(check.Ok);
        Assert.Contains(check.Issues, i => i.Code == "INSUFFICIENT_STOCK");
    }

    [Fact]
    public void Order_over_available_credit_is_rejected()
    {
        // C30000: limit 10,000, balance 8,500, open orders 990 => 510 available. 3 x 189 = 567.
        var check = _orders.Validate("C30000", [new("A00001", 3)]);

        Assert.False(check.Ok);
        Assert.Equal(510m, check.Credit!.Available);
        Assert.Contains(check.Issues, i => i.Code == "CREDIT_LIMIT_EXCEEDED");
    }

    [Fact]
    public void Unknown_customer_and_item_are_reported()
    {
        var check = _orders.Validate("C99999", [new("Z99999", 1)]);

        Assert.False(check.Ok);
        Assert.Contains(check.Issues, i => i.Code == "CUSTOMER_NOT_FOUND");
        Assert.Contains(check.Issues, i => i.Code == "ITEM_NOT_FOUND");
    }

    [Fact]
    public void Duplicate_lines_are_merged_before_the_stock_check()
    {
        // A00006: 3 on hand, 1 committed => 2 available. 1 + 2 = 3 must fail.
        var check = _orders.Validate("C20000", [new("A00006", 1), new("a00006", 2)]);

        Assert.False(check.Ok);
        Assert.Single(check.Lines);
        Assert.Equal(3, check.Lines[0].Quantity);
    }

    [Fact]
    public void Rejected_create_writes_no_order_but_is_audited()
    {
        var ordersBefore = Count("SELECT COUNT(*) FROM Orders");

        var result = _orders.Create("C30000", [new("A00001", 3)]);

        Assert.False(result.Created);
        Assert.Equal(ordersBefore, Count("SELECT COUNT(*) FROM Orders"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM AuditLog WHERE Action = 'create_sales_order.rejected'"));
    }

    [Fact]
    public void Same_request_id_creates_the_order_once()
    {
        var ordersBefore = Count("SELECT COUNT(*) FROM Orders");

        var first = _orders.Create("C40000", [new("A00002", 1)], requestId: "run-1:call-1");
        var retry = _orders.Create("C40000", [new("A00002", 1)], requestId: "run-1:call-1");

        Assert.True(first.Created);
        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.DocNum, retry.DocNum);
        Assert.Equal(ordersBefore + 1, Count("SELECT COUNT(*) FROM Orders"));
    }

    private int Committed(string itemCode)
    {
        using var c = _db.Open();
        return Convert.ToInt32(Sql.Scalar(c, null, "SELECT Committed FROM Items WHERE ItemCode = $code", new { code = itemCode }));
    }

    private long Count(string sql)
    {
        using var c = _db.Open();
        return Convert.ToInt64(Sql.Scalar(c, null, sql));
    }
}
