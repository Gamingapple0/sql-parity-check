using Parity;

const string usage = """
    parity exec       <db> <file.sql>     run a SQL file (schema, seed)
    parity copy       <mssql-db> <pg-db>  copy every table
    parity compare    <db-a> <db-b>       row counts and per-table hashes; exit 0 = parity, 1 = difference

    <db> is mssql:<connection string> or pg:<connection string>.
    """;

try
{
    switch (args)
    {
        case ["exec", var db, var file]:
            using (var c = Db.Open(db)) Db.Exec(c, File.ReadAllText(file));
            return 0;
        case ["copy", var from, var to]:
            Migration.Copy(from, to);
            return 0;
        case ["compare", var a, var b]:
            return Migration.Compare(a, b) ? 0 : 1;
        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}
catch (Exception e)
{
    // Exit 2, not 1: an error is neither parity nor a proven difference, and still blocks a cutover.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}
