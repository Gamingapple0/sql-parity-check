using Parity;

const string usage = """
    parity exec       <db> <file.sql>     run a SQL file (schema, seed)

    <db> is mssql:<connection string> or pg:<connection string>.
    """;

try
{
    switch (args)
    {
        case ["exec", var db, var file]:
            using (var c = Db.Open(db)) Db.Exec(c, File.ReadAllText(file));
            return 0;
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
