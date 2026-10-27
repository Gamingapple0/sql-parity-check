using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Parity;

// A database is named as "<kind>:<connection string>":
//   mssql:Server=localhost,1433;...   SQL Server, or Babelfish (it speaks the SQL Server protocol)
//   pg:Host=localhost;...             PostgreSQL, including RDS for PostgreSQL
public static class Db
{
    public static DbConnection Open(string db)
    {
        var colon = db.IndexOf(':');
        var (kind, conn) = colon < 0 ? ("", db) : (db[..colon], db[(colon + 1)..]);
        DbConnection c = kind switch
        {
            "mssql" => new SqlConnection(conn),
            "pg" => new NpgsqlConnection(conn),
            _ => throw new ArgumentException($"Name the engine first: mssql:<connection string> or pg:<connection string>, got '{kind}'"),
        };
        c.Open();
        return c;
    }

    public static object? Scalar(DbConnection c, string sql, DbTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
