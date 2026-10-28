using Parity;

namespace Parity.Tests;

[Collection("engines")]
public class ParityTests(Engines engines)
{

    [Fact]
    public void Copy_then_compare_reports_parity()
    {
        Migration.Copy(engines.SqlServer, engines.Postgres);
        Assert.True(Migration.Compare(engines.SqlServer, engines.Postgres));
    }

    // The gate is only worth something if it can fail: a one-cent change in one row must be caught.
    [Fact]
    public void Compare_fails_when_one_value_differs()
    {
        Migration.Copy(engines.SqlServer, engines.Postgres);
        using (var pg = Db.Open(engines.Postgres))
            Db.Exec(pg, "UPDATE line_items SET amount = amount + 0.01 WHERE id = 42");
        Assert.False(Migration.Compare(engines.SqlServer, engines.Postgres));
    }

    // The posted migration in miniature: self-managed SQL Server to a managed SQL Server
    // (here a second database), then to PostgreSQL with Babelfish through port 1433.
    [Fact]
    public void Two_hops_reach_Babelfish_and_both_ports_see_the_data()
    {
        Migration.Copy(engines.SqlServer, engines.SqlServerHop1);
        Assert.True(Migration.Compare(engines.SqlServer, engines.SqlServerHop1));

        Migration.Copy(engines.SqlServerHop1, engines.Babelfish);
        Assert.True(Migration.Compare(engines.SqlServerHop1, engines.Babelfish));

        // Old code keeps talking T-SQL on 1433; new code reads the same rows as plain PostgreSQL on 5432.
        using var tsql = Db.Open(engines.Babelfish);
        using var pg = Db.Open(engines.BabelfishPostgres);
        var schema = Db.Scalar(pg, "SELECT table_schema FROM information_schema.tables WHERE table_name = 'line_items'");
        Assert.Equal(Convert.ToInt64(Db.Scalar(tsql, "SELECT COUNT(*) FROM line_items")),
                     Db.Scalar(pg, $"SELECT count(*) FROM {schema}.line_items"));
    }
}
