using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Parity;

public static class Migration
{
    // Parents before children, so foreign keys hold during the copy.
    public static readonly string[] Tables = ["customers", "invoices", "line_items"];

    public static void Copy(string source, string target)
    {
        using var src = (SqlConnection)Db.Open(source);
        using var dst = (NpgsqlConnection)Db.Open(target);

        Db.Exec(dst, $"TRUNCATE {string.Join(", ", Tables)}");
        foreach (var table in Tables)
        {
            var sw = Stopwatch.StartNew();
            using var cmd = new SqlCommand($"SELECT * FROM {table} ORDER BY id", src);
            using var r = cmd.ExecuteReader();
            var cols = string.Join(", ", Enumerable.Range(0, r.FieldCount).Select(r.GetName));
            using var w = dst.BeginBinaryImport($"COPY {table} ({cols}) FROM STDIN (FORMAT BINARY)");
            long rows = 0;
            while (r.Read())
            {
                w.StartRow();
                for (var i = 0; i < r.FieldCount; i++)
                {
                    if (r.IsDBNull(i)) w.WriteNull();
                    else w.Write(r.GetValue(i));
                }
                rows++;
            }
            w.Complete();
            Console.WriteLine($"{table,-12} {rows,8} rows copied   {sw.ElapsedMilliseconds,6} ms");
        }
    }

    // Parity = same row count and same hash per table. Rows are read in integer key order
    // (string keys would sort by collation, which differs between engines) and each value is
    // normalised so the hash compares data, not driver formatting.
    public static bool Compare(string a, string b)
    {
        using var ca = Db.Open(a);
        using var cb = Db.Open(b);
        var same = true;
        foreach (var table in Tables)
        {
            var sw = Stopwatch.StartNew();
            var (rowsA, hashA) = Hash(ca, table);
            var (rowsB, hashB) = Hash(cb, table);
            var ok = rowsA == rowsB && hashA == hashB;
            same &= ok;
            Console.WriteLine($"{table,-12} rows {rowsA,8} vs {rowsB,-8} hash {(hashA == hashB ? "match" : "DIFF "),-5}  {sw.ElapsedMilliseconds,6} ms");
        }
        Console.WriteLine(same ? "PARITY" : "DIFFERENCE");
        return same;
    }

    static (long Rows, string Hash) Hash(DbConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} ORDER BY id";
        using var r = cmd.ExecuteReader();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rows = 0;
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++)
                h.AppendData(Encoding.UTF8.GetBytes(Canonical(r.GetValue(i)) + "\u001f"));
            h.AppendData("\u001e"u8);
            rows++;
        }
        return (rows, Convert.ToHexString(h.GetHashAndReset()));
    }

    public static string Canonical(object v) => v switch
    {
        DBNull => "␀",                                                   // distinct from ''
        decimal d => d.ToString("0.##########", CultureInfo.InvariantCulture), // 12.50 and 12.5 are equal
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",                                               // bit and boolean
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()!,                                                    // strings as stored
    };
}
