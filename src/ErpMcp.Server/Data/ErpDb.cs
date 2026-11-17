using Microsoft.Data.Sqlite;

namespace ErpMcp.Server.Data;

/// <summary>
/// A deliberately small ERP schema, named after SAP Business One concepts
/// (CardCode, ItemCode, DocNum, OnHand / Committed).
/// </summary>
public sealed class ErpDb(string connectionString)
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Customers (
            CardCode    TEXT PRIMARY KEY,
            CardName    TEXT NOT NULL,
            CreditLimit REAL NOT NULL,
            Balance     REAL NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Items (
            ItemCode  TEXT PRIMARY KEY,
            ItemName  TEXT NOT NULL,
            Price     REAL NOT NULL,
            OnHand    INTEGER NOT NULL,
            Committed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Orders (
            DocNum    INTEGER PRIMARY KEY AUTOINCREMENT,
            CardCode  TEXT NOT NULL REFERENCES Customers(CardCode),
            Status    TEXT NOT NULL DEFAULT 'Open',
            DocTotal  REAL NOT NULL,
            CreatedAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS OrderLines (
            DocNum   INTEGER NOT NULL REFERENCES Orders(DocNum),
            LineNum  INTEGER NOT NULL,
            ItemCode TEXT NOT NULL REFERENCES Items(ItemCode),
            Quantity INTEGER NOT NULL,
            Price    REAL NOT NULL,
            PRIMARY KEY (DocNum, LineNum)
        );
        CREATE TABLE IF NOT EXISTS AuditLog (
            Id      INTEGER PRIMARY KEY AUTOINCREMENT,
            At      TEXT NOT NULL,
            Action  TEXT NOT NULL,
            Payload TEXT NOT NULL
        );
        """;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        Sql.Execute(c, null, "PRAGMA foreign_keys = ON;");
        return c;
    }

    public void EnsureCreatedAndSeeded()
    {
        using var c = Open();
        Sql.Execute(c, null, Schema);

        // Additive migration for databases created before idempotent order creation existed.
        var orderColumns = Sql.Query(c, null, "SELECT name FROM pragma_table_info('Orders')", r => r.GetString(0));
        if (!orderColumns.Contains("RequestId"))
        {
            Sql.Execute(c, null, "ALTER TABLE Orders ADD COLUMN RequestId TEXT");
        }
        Sql.Execute(c, null, "CREATE UNIQUE INDEX IF NOT EXISTS IX_Orders_RequestId ON Orders(RequestId)");
        var items = Convert.ToInt64(Sql.Scalar(c, null, "SELECT COUNT(*) FROM Items"));
        if (items == 0)
        {
            Seed(c);
        }
    }

    private static void Seed(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();

        (string Code, string Name, decimal Limit, decimal Balance)[] customers =
        [
            ("C20000", "Danube Office Supply Kft", 50_000m, 12_000m),
            ("C30000", "Tagus Retail Lda", 10_000m, 8_500m),      // close to its credit limit
            ("C40000", "Nordic Furnishings AS", 25_000m, 0m),
            ("C50000", "Alpine Workspace GmbH", 15_000m, 3_000m),
        ];
        foreach (var cu in customers)
        {
            Sql.Execute(c, tx,
                "INSERT INTO Customers (CardCode, CardName, CreditLimit, Balance) VALUES ($code, $name, $limit, $balance)",
                new { code = cu.Code, name = cu.Name, limit = cu.Limit, balance = cu.Balance });
        }

        (string Code, string Name, decimal Price, int OnHand, int Committed)[] items =
        [
            ("A00001", "Ergonomic office chair", 189.00m, 40, 5),
            ("A00002", "Standing desk 160x80", 449.00m, 12, 2),
            ("A00003", "Dual monitor arm", 79.90m, 60, 0),
            ("A00004", "27\" 4K monitor", 329.00m, 25, 10),
            ("A00005", "Wireless keyboard", 49.90m, 120, 0),
            ("A00006", "Conference table 240x120", 1290.00m, 3, 1),
            ("A00007", "LED desk lamp", 34.50m, 0, 0),             // out of stock
            ("A00008", "Basic office chair", 99.00m, 80, 10),      // 10 committed to the seeded open order
        ];
        foreach (var it in items)
        {
            Sql.Execute(c, tx,
                "INSERT INTO Items (ItemCode, ItemName, Price, OnHand, Committed) VALUES ($code, $name, $price, $onHand, $committed)",
                new { code = it.Code, name = it.Name, price = it.Price, onHand = it.OnHand, committed = it.Committed });
        }

        // One open order so the near-limit customer has real credit exposure.
        Sql.Execute(c, tx,
            "INSERT INTO Orders (CardCode, Status, DocTotal, CreatedAt) VALUES ('C30000', 'Open', 990, $at)",
            new { at = DateTime.UtcNow.AddDays(-3).ToString("O") });
        var docNum = Convert.ToInt64(Sql.Scalar(c, tx, "SELECT last_insert_rowid()"));
        Sql.Execute(c, tx,
            "INSERT INTO OrderLines (DocNum, LineNum, ItemCode, Quantity, Price) VALUES ($doc, 0, 'A00008', 10, 99)",
            new { doc = docNum });

        tx.Commit();
    }
}
