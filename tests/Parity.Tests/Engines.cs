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
    readonly MsSqlContainer _mssql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("MSSQL_AGENT_ENABLED", "true") // CDC capture runs as an Agent job
        .Build();
    readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16").Build();
    readonly IContainer _babelfish = new ContainerBuilder("jonathanpotts/babelfishpg")
        .WithPortBinding(1433, true)
        .Build();

    public string SqlServer { get; private set; } = "";
    public string Postgres { get; private set; } = "";
    public string Babelfish { get; private set; } = "";

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
        using (var c = Db.Open("mssql:" + cs)) Db.Exec(c, "CREATE DATABASE parity"); // CDC can't run in master
        cs.InitialCatalog = "parity";
        SqlServer = "mssql:" + cs;
        Postgres = "pg:" + _pg.GetConnectionString();
        Babelfish = $"mssql:Server={_babelfish.Hostname},{_babelfish.GetMappedPublicPort(1433)};" +
                    "User ID=babelfish_user;Password=12345678;Encrypt=False"; // this image has no TLS

        using (var c = Db.Open(SqlServer))
        {
            Db.Exec(c, Sql("schema.mssql.sql"));
            Db.Exec(c, Sql("seed.mssql.sql"));
        }
        using (var c = Db.Open(Postgres)) Db.Exec(c, Sql("schema.pg.sql"));

        // The Babelfish image initialises its cluster on first start, so the port opens before T-SQL is ready.
        for (var i = 0; ; i++)
        {
            try { using var c = Db.Open(Babelfish); Db.Scalar(c, "SELECT 1"); break; }
            catch (Exception) when (i < 60) { await Task.Delay(1000); }
        }
        using (var c = Db.Open(Babelfish))
            if (Db.Scalar(c, "SELECT @@VERSION") is not string v || !v.Contains("Babelfish"))
                throw new InvalidOperationException("Port 1433 of the Babelfish container is not Babelfish");
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
