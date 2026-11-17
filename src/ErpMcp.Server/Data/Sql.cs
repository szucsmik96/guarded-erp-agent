using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ErpMcp.Server.Data;

/// <summary>Tiny Dapper-style helpers. Parameters come from an anonymous object: new { code = "A1" } binds $code.</summary>
internal static class Sql
{
    public static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, object? args = null)
    {
        using var cmd = Create(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, object? args = null)
    {
        using var cmd = Create(c, tx, sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public static List<T> Query<T>(
        SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, object? args = null)
    {
        using var cmd = Create(c, tx, sql, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    private static SqliteCommand Create(SqliteConnection c, SqliteTransaction? tx, string sql, object? args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                cmd.Parameters.AddWithValue("$" + p.Name, p.GetValue(args) ?? DBNull.Value);
            }
        }
        return cmd;
    }
}
