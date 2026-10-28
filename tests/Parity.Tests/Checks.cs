using Microsoft.Data.SqlClient;
using Npgsql;
using Parity;

namespace Parity.Tests;

// One check = the same question asked in T-SQL (SQL Server, Babelfish) and in PostgreSQL SQL,
// with the answer each engine actually gives. Answers are normalised to text:
// true/false -> 1/0, NULL -> "NULL", a duplicate-key error -> "unique violation".
public record Check(int Id, string Name, string TSql, string Pg, string SqlServer, string Postgres, string Babelfish,
                    string? PgRemedy = null, string? TSqlForRemedy = null);

[Collection("engines")]
public class Checks(Engines engines)
{
    public static readonly Check[] All =
    [
        new(1, "Case in comparisons (default collation)",
            "SELECT CASE WHEN 'Anshu' = 'anshu' THEN 1 ELSE 0 END",
            "SELECT 'Anshu' = 'anshu'",
            SqlServer: "1", Postgres: "0", Babelfish: "1",
            PgRemedy: """
                CREATE COLLATION IF NOT EXISTS ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                CREATE TEMP TABLE t (name varchar(50) COLLATE ci);
                INSERT INTO t VALUES ('Anshu');
                SELECT count(*) FROM t WHERE name = 'anshu';
                """),

        new(2, "Trailing spaces in =",
            "SELECT CASE WHEN 'abc' = 'abc  ' THEN 1 ELSE 0 END",
            "SELECT 'abc'::varchar = 'abc  '::varchar",
            SqlServer: "1", Postgres: "0", Babelfish: "1"),

        new(3, "Trailing spaces in length",
            "SELECT LEN('abc  ')",
            "SELECT length('abc  '::varchar)",
            SqlServer: "3", Postgres: "5", Babelfish: "3"),

        new(4, "NULLs in a unique index",
            """
            CREATE TABLE #t (code varchar(10) NULL);
            CREATE UNIQUE INDEX ux_code ON #t (code);
            INSERT INTO #t VALUES (NULL);
            INSERT INTO #t VALUES (NULL);
            SELECT COUNT(*) FROM #t;
            """,
            """
            CREATE TEMP TABLE t (code varchar(10) NULL);
            CREATE UNIQUE INDEX ON t (code);
            INSERT INTO t VALUES (NULL);
            INSERT INTO t VALUES (NULL);
            SELECT count(*) FROM t;
            """,
            SqlServer: "unique violation", Postgres: "2", Babelfish: "unique violation",
            PgRemedy: """
                CREATE TEMP TABLE t (code varchar(10) NULL);
                CREATE UNIQUE INDEX ON t (code) NULLS NOT DISTINCT;
                INSERT INTO t VALUES (NULL);
                INSERT INTO t VALUES (NULL);
                SELECT count(*) FROM t;
                """),

        new(5, "NULL sort order",
            "SELECT TOP 1 x FROM (VALUES (1), (NULL)) v(x) ORDER BY x",
            "SELECT x FROM (VALUES (1), (NULL)) v(x) ORDER BY x LIMIT 1",
            SqlServer: "NULL", Postgres: "1", Babelfish: "NULL",
            PgRemedy: "SELECT x FROM (VALUES (1), (NULL)) v(x) ORDER BY x NULLS FIRST LIMIT 1"),

        new(6, "Decimal to integer cast",
            "SELECT CAST(2.5 AS int)",
            "SELECT CAST(2.5 AS int)",
            SqlServer: "2", Postgres: "3", Babelfish: "2",
            PgRemedy: "SELECT trunc(2.5)::int"),

        new(7, "Datetime precision",
            "SELECT CONVERT(varchar(30), CAST('2026-01-01T10:00:00.001' AS datetime), 121)",
            "SELECT to_char(CAST('2026-01-01 10:00:00.001' AS timestamp), 'YYYY-MM-DD HH24:MI:SS.MS')",
            SqlServer: "2026-01-01 10:00:00.000", Postgres: "2026-01-01 10:00:00.001", Babelfish: "2026-01-01 10:00:00.000",
            PgRemedy: "SELECT to_char(CAST('2026-01-01 10:00:00.123456' AS timestamp(6)), 'YYYY-MM-DD HH24:MI:SS.US')",
            TSqlForRemedy: "SELECT CONVERT(varchar(30), CAST('2026-01-01T10:00:00.123456' AS datetime2(6)), 121)"),

        new(8, "Control: empty string versus NULL",
            "SELECT CASE WHEN '' IS NULL THEN 1 ELSE 0 END",
            "SELECT '' IS NULL",
            SqlServer: "0", Postgres: "0", Babelfish: "0"),

        // Instances where Babelfish does not mimic SQL Server's behaviour
        new(9, "datetime2(7) beyond microseconds",
            "SELECT CONVERT(varchar(30), CAST('2026-01-01T10:00:00.1234567' AS datetime2(7)), 121)",
            "SELECT to_char(CAST('2026-01-01 10:00:00.1234567' AS timestamp), 'YYYY-MM-DD HH24:MI:SS.US')",
            SqlServer: "2026-01-01 10:00:00.1234567", Postgres: "2026-01-01 10:00:00.123457", Babelfish: "2026-01-01 10:00:00.123457"),

        new(10, "DATALENGTH of nvarchar",
            "SELECT DATALENGTH(N'abc')",
            "SELECT octet_length('abc')",
            SqlServer: "6", Postgres: "3", Babelfish: "3"),
    ];

    public static TheoryData<int, string, Engine> Cases()
    {
        var data = new TheoryData<int, string, Engine>();
        foreach (var c in All)
            foreach (var e in Enum.GetValues<Engine>())
                data.Add(c.Id, c.Name, e);
        return data;
    }

    public static TheoryData<int, string> Remedies()
    {
        var data = new TheoryData<int, string>();
        foreach (var c in All.Where(c => c.PgRemedy != null)) data.Add(c.Id, c.Name);
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Engine_behaves_as_recorded(int id, string name, Engine engine)
    {
        var c = All.Single(c => c.Id == id);
        var (sql, expected) = engine switch
        {
            Engine.SqlServer => (c.TSql, c.SqlServer),
            Engine.Postgres => (c.Pg, c.Postgres),
            _ => (c.TSql, c.Babelfish),
        };
        Assert.True(expected == Run(engines[engine], sql), $"{name} on {engine}: expected {expected}, got {Run(engines[engine], sql)}");
    }

    // A remedy passes when PostgreSQL, with the fix, gives the answer SQL Server gives.
    [Theory, MemberData(nameof(Remedies))]
    public void Postgres_remedy_matches_SqlServer(int id, string name)
    {
        var c = All.Single(c => c.Id == id);
        var sqlServer = Run(engines.SqlServer, c.TSqlForRemedy ?? c.TSql);
        Assert.True(sqlServer == Run(engines.Postgres, c.PgRemedy!), $"{name}: SQL Server {sqlServer}, PostgreSQL with remedy {Run(engines.Postgres, c.PgRemedy!)}");
    }

    public static string Run(string db, string sql)
    {
        using var c = Db.Open(db);
        try
        {
            return Db.Scalar(c, sql) switch
            {
                null => "NULL",
                bool b => b ? "1" : "0",
                var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!,
            };
        }
        catch (SqlException e) when (e.Number is 2601 or 2627) { return "unique violation"; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return "unique violation"; }
        catch (SqlException e) { return $"error {e.Number}: {e.Message}"; }
        catch (PostgresException e) { return $"error {e.SqlState}: {e.MessageText}"; }
    }
}
