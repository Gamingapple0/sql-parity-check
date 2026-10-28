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

}
