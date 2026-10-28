using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Parity;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Parity.Tests;

public enum Engine { SqlServer, Postgres, Babelfish }

// Starts all three engines once for the whole test run and loads the sample schema and data:
// schema + seed into SQL Server (the source), schema only into PostgreSQL (the target).
public sealed class Engines : IAsyncLifetime
{
    readonly MsSqlContainer _mssql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16").Build();
    readonly IContainer _babelfish = new ContainerBuilder("jonathanpotts/babelfishpg")
        .WithPortBinding(1433, true) // T-SQL (TDS)
        .WithPortBinding(5432, true) // plain PostgreSQL, same data
        .Build();

    public string SqlServer { get; private set; } = "";
    public string Postgres { get; private set; } = "";
    public string Babelfish { get; private set; } = "";
    // Second database on the SQL Server container: stands in for RDS for SQL Server in the two-hop test.
    public string SqlServerHop1 { get; private set; } = "";
    // The same Babelfish database, reached through PostgreSQL's own port.
    public string BabelfishPostgres { get; private set; } = "";

    public string this[Engine e] => e switch
    {
        Engine.SqlServer => SqlServer,
        Engine.Postgres => Postgres,
        _ => Babelfish,
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mssql.StartAsync(), _pg.StartAsync(), _babelfish.StartAsync());

        var cs = new SqlConnectionStringBuilder(_mssql.GetConnectionString());
        using (var c = Db.Open("mssql:" + cs)) Db.Exec(c, "CREATE DATABASE parity; CREATE DATABASE parity_hop1");
        cs.InitialCatalog = "parity";
        SqlServer = "mssql:" + cs;
        cs.InitialCatalog = "parity_hop1";
        SqlServerHop1 = "mssql:" + cs;
        Postgres = "pg:" + _pg.GetConnectionString();

        using (var c = Db.Open(SqlServer))
        {
            Db.Exec(c, Sql("schema.mssql.sql"));
            Db.Exec(c, Sql("seed.mssql.sql"));
        }
        using (var c = Db.Open(SqlServerHop1)) Db.Exec(c, Sql("schema.mssql.sql"));
        using (var c = Db.Open(Postgres)) Db.Exec(c, Sql("schema.pg.sql"));

        // The Babelfish image initialises its cluster on first start, so the port opens before T-SQL is ready.
        var babelfishServer = $"mssql:Server={_babelfish.Hostname},{_babelfish.GetMappedPublicPort(1433)};" +
                              "User ID=babelfish_user;Password=12345678;Encrypt=False"; // this image has no TLS
        for (var i = 0; ; i++)
        {
            try { using var c = Db.Open(babelfishServer); Db.Scalar(c, "SELECT 1"); break; }
            catch (Exception) when (i < 60) { await Task.Delay(1000); }
        }
        using (var c = Db.Open(babelfishServer))
        {
            if (Db.Scalar(c, "SELECT @@VERSION") is not string v || !v.Contains("Babelfish"))
                throw new InvalidOperationException("Port 1433 of the Babelfish container is not Babelfish");
            Db.Exec(c, "CREATE DATABASE parity");
        }
        // The SQL Server schema script runs unchanged on Babelfish: that's its point.
        Babelfish = babelfishServer + ";Database=parity";
        using (var c = Db.Open(Babelfish)) Db.Exec(c, Sql("schema.mssql.sql"));
        BabelfishPostgres = $"pg:Host={_babelfish.Hostname};Port={_babelfish.GetMappedPublicPort(5432)};" +
                            "Database=babelfish_db;Username=babelfish_user;Password=12345678";
    }

    public static string Sql(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sql", file));

    public async Task DisposeAsync()
    {
        await _mssql.DisposeAsync();
        await _pg.DisposeAsync();
        await _babelfish.DisposeAsync();
    }
}

[CollectionDefinition("engines")]
public class EnginesCollection : ICollectionFixture<Engines>;
