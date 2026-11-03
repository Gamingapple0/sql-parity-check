# sql-parity-check

Same data, same queries, two databases: where do SQL Server and PostgreSQL silently give different answers?

A migration can move every row correctly and still change behaviour. No errors, no failed jobs, just a query that returns something different after cutover. This repo runs the same checks on SQL Server, PostgreSQL and Babelfish (PostgreSQL with SQL Server compatibility), proves a PostgreSQL fix for each difference, and migrates a sample database in two hops, self-managed SQL Server to Amazon RDS for SQL Server, then to Aurora PostgreSQL with Babelfish, with a small parity tool that copies data and gates each cutover on row counts and per-table hashes.

It's a learning project on SQL Server to PostgreSQL migrations, built with [Claude Code](https://claude.com/claude-code).

## What it found

| # | Difference | SQL Server | PostgreSQL | Babelfish |
|---|---|---|---|---|
| 1 | Case in `=` | `'Anshu' = 'anshu'` true | false | true |
| 2 | Trailing spaces in `=` | equal | not equal | equal |
| 3 | `LEN('abc  ')` | 3 | 5 | 3 |
| 4 | Two NULLs in a unique index | rejected | accepted | rejected |
| 5 | NULLs in `ORDER BY` | first | last | first |
| 6 | `CAST(2.5 AS int)` | 2 | 3 | 2 |
| 7 | `datetime` with `.001` | `.000` | `.001` | `.000` |
| 8 | Control: `''` is NULL | no | no | no |
| 9 | `datetime2(7)` | 7 digits kept | rounded to 6 | rounded to 6 |
| 10 | `DATALENGTH(N'abc')` | 6 | 3 | 3 |

Babelfish hides checks 1 to 7; 9 and 10 are where it behaves like PostgreSQL underneath. Details, causes and fixes for each are in [DIFFERENCES.md](DIFFERENCES.md).

## Run the checks

Needs Docker and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
```

Testcontainers starts SQL Server 2022, PostgreSQL 16 and Babelfish, loads the sample data, and runs 38 tests:

- **Checks** (`tests/Parity.Tests/Checks.cs`): each difference asked in T-SQL and in PostgreSQL SQL, with the answer every engine gave pinned as the expected result.
- **Remedies**: for checks 1, 4, 5, 6 and 7, PostgreSQL with the fix must return what SQL Server returns.
- **Parity tool** (`tests/Parity.Tests/ParityTests.cs`): copy then compare passes; a one-cent change in one row makes compare fail; and the two-hop migration ends in Babelfish with ports 1433 and 5432 seeing the same rows.

The first run pulls about 3.5 GB of images.

## The migration

```
self-managed SQL Server ──hop 1──▶ RDS for SQL Server ──hop 2──▶ Aurora PostgreSQL + Babelfish
 (Developer, local)      full load    (Express)          full load     1433: T-SQL, existing code
                         + compare                       + compare     5432: PostgreSQL, new code
```

- **Hop 1** changes where SQL Server runs, not the engine.
- **Hop 2** changes the engine. The SQL Server schema script runs unchanged against Babelfish on 1433, data goes in through 1433, and the same rows are readable as plain PostgreSQL on 5432.
- Each hop is an offline cutover: stop writes, copy, compare, switch. At this size a copy takes under a second, so keeping the target in sync while the source stays live (change data capture) isn't needed. A write that lands after the copy makes `compare` fail, which is the point of the gate.

## The parity tool

```
dotnet run --project src/Parity -- <command>

exec       <db> <file.sql>     run a SQL file (schema, seed)
copy       <mssql-db> <db>     copy every table
compare    <db-a> <db-b>       row counts and per-table hashes; exit 0 = parity, 1 = difference
```

A database is written `mssql:<connection string>` or `pg:<connection string>`. SQL Server, RDS for SQL Server and Babelfish's port 1433 are `mssql`; PostgreSQL and Babelfish's port 5432 are `pg`.

- **copy** reads each table from SQL Server in key order and writes it with PostgreSQL's binary `COPY` or SQL Server bulk copy, depending on the target.
- **compare** reads both sides in integer key order, normalises each value (decimals by value, timestamps to microseconds, `bit` and `boolean` as 1/0, strings as stored) and hashes each table with SHA-256. Integer keys matter here: ordering by a string key would follow each engine's collation and differ. Any error exits 2, so a broken run can't pass as parity.

### Try it locally

Three containers stand in for the three stages:

```
docker run -d --name parity-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Parity!Passw0rd' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
docker run -d --name parity-rds -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Parity!Passw0rd' -e MSSQL_PID=Express -p 1434:1433 mcr.microsoft.com/mssql/server:2022-latest
docker run -d --name parity-bbf -p 1435:1433 -p 5433:5432 jonathanpotts/babelfishpg
```

Create a `parity` database on each (`CREATE DATABASE parity`), then, in PowerShell:

```
$SRC  = 'mssql:Server=localhost,1433;Database=parity;User ID=sa;Password=Parity!Passw0rd;TrustServerCertificate=True'
$HOP1 = 'mssql:Server=localhost,1434;Database=parity;User ID=sa;Password=Parity!Passw0rd;TrustServerCertificate=True'
$HOP2 = 'mssql:Server=localhost,1435;Database=parity;User ID=babelfish_user;Password=12345678;Encrypt=False'

dotnet run --project src/Parity -- exec "$SRC"  sql/schema.mssql.sql
dotnet run --project src/Parity -- exec "$HOP1" sql/schema.mssql.sql
dotnet run --project src/Parity -- exec "$HOP2" sql/schema.mssql.sql
dotnet run --project src/Parity -- exec "$SRC"  sql/seed.mssql.sql

dotnet run --project src/Parity -- copy "$SRC" "$HOP1"
dotnet run --project src/Parity -- compare "$SRC" "$HOP1"    # exit code 0 = parity

dotnet run --project src/Parity -- copy "$HOP1" "$HOP2"
dotnet run --project src/Parity -- compare "$HOP1" "$HOP2"
```

The tables are then readable on port 5433 as plain PostgreSQL, in schema `dbo` of database `babelfish_db`.

## Cloud migration

[`infra/`](infra/) is an AWS CDK app in C# with one stack per hop, both in the default VPC, reachable only from one IP, passwords generated into Secrets Manager:

- `Hop1SqlServer`: RDS for SQL Server 2022 Express, db.t3.micro, 20 GB, single-AZ, no backups kept.
- `Hop2Babelfish`: Aurora PostgreSQL 16 Serverless v2 (0.5 to 1 ACU) with Babelfish on, single-db mode, encrypted, the minimum one-day backup.

Both are built to be created for one session and destroyed:

```
cd infra
cdk bootstrap -c myIp=<your public ip>/32
cdk diff      Hop1SqlServer Hop2Babelfish -c myIp=<your public ip>/32
cdk deploy    Hop1SqlServer Hop2Babelfish -c myIp=<your public ip>/32
# create the database and schema on both, then hop 1 and hop 2 as above
cdk destroy   Hop1SqlServer Hop2Babelfish -c myIp=<your public ip>/32
```

The cloud run reached parity on both hops, and port 5432 on Aurora saw the same 50, 200 and 600 rows. One thing the local rehearsal didn't show: on db.t3.micro, hop 1's bulk insert stalled waiting for a query memory grant (`RESOURCE_SEMAPHORE`). The fix tells SQL Server the rows already arrive in key order, so it needs no sort. Babelfish rejects that hint, so `copy` only sends it to real SQL Server. [MIGRATION.md](MIGRATION.md) records the run.

## Sample data

Generic accounting data: 50 customers, 200 invoices, 600 line items, generated by [`sql/seed.mssql.sql`](sql/seed.mssql.sql). It includes the values a migration has to carry intact: trailing spaces in codes, mixed-case emails, NULLs, non-ASCII names and microsecond timestamps. Every table has an integer surrogate key. Timestamps are `datetime2(6)` on SQL Server and `timestamp(6)` on PostgreSQL, so the copy is lossless.

## Layout

| Path | Purpose |
|---|---|
| `sql/` | Schema per engine and the seed script |
| `src/Parity/` | The parity tool: copy and compare |
| `tests/Parity.Tests/` | Engine fixture, the checks and remedies, parity tests |
| `infra/` | CDK stacks for the two hops |
| `DIFFERENCES.md` | Each difference: what happens, why it's silent, how to detect and fix it |
| `MIGRATION.md` | The migration run: steps, timings, results, cost |

## Limits

This is the smallest end-to-end version, not a migration product.

- Offline cutovers only: no change data capture, so writes must stop during each copy. It doesn't use AWS DMS or native backup and restore.
- Demo networking: public endpoints locked to one IP.
- Tiny data. `compare` reads whole tables; large tables would need keyset chunks.
- Locally, Babelfish runs from a community image (`jonathanpotts/babelfishpg`).
- SQL Server runs locally as the Developer edition from Microsoft's official image, which is licensed for development and testing.

## Next steps

- **Online cutover:** AWS DMS full load plus change data capture, so the source stays live and the switch takes seconds. On RDS for SQL Server that needs Standard or Enterprise edition, since Express has no CDC.
- **Rollback path:** reverse replication from the new target to the old one until the cutover is proven.
- **Schema and code conversion at scale:** Babelfish Compass to find unsupported T-SQL, and AWS DMS Schema Conversion for what Babelfish can't run.
- **Validation at scale:** keyset-chunked hashing for large tables, plus the behaviour checks run against real queries.
- **Production networking:** private subnets, no public endpoints, IAM and secrets rotation.
- **Not yet covered:** locking versus MVCC, identity and sequence values after a bulk load, T-SQL procedure conversion.
