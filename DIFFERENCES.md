# Silent differences: SQL Server vs PostgreSQL (and Babelfish)

Every result below was produced by `dotnet test` against SQL Server 2022, PostgreSQL 16 and the community Babelfish image, not copied from documentation. The checks live in [`tests/Parity.Tests/Checks.cs`](tests/Parity.Tests/Checks.cs); each one asks the same question in T-SQL and in PostgreSQL SQL.

Babelfish is PostgreSQL with a SQL Server compatibility layer. It accepts T-SQL on port 1433, so it runs the T-SQL version of each check.

## Results

| # | Difference | SQL Server | PostgreSQL | Babelfish | PostgreSQL remedy |
|---|---|---|---|---|---|
| 1 | Case in `=` (default collation) | `'Anshu' = 'anshu'` true | false | true | Nondeterministic ICU collation on the column |
| 2 | Trailing spaces in `=` | `'abc' = 'abc  '` true | false | true | Detect only |
| 3 | Trailing spaces in length | `LEN('abc  ')` = 3 | `length` = 5 | 3 | Detect only |
| 4 | Two NULLs in a unique index | rejected | both accepted | rejected | `UNIQUE ... NULLS NOT DISTINCT` |
| 5 | NULLs in `ORDER BY x` | first | last | first | `ORDER BY x NULLS FIRST` |
| 6 | `CAST(2.5 AS int)` | 2 (truncates) | 3 (rounds) | 2 | `trunc(x)::int` |
| 7 | `datetime` precision | `.001` stored as `.000` | `.001` kept | `.000` | Map `datetime2(6)` to `timestamp(6)` |
| 8 | Control: `''` is NULL? | no | no | no | Not needed |
| 9 | `datetime2(7)` | `.1234567` kept | `.123457` | `.123457` | None: PostgreSQL stops at microseconds |
| 10 | `DATALENGTH(N'abc')` | 6 (UTF-16) | 3 (UTF-8) | 3 | Detect only |

Every remedy is a test too: PostgreSQL with the fix must return what SQL Server returns.

**Babelfish hid all of checks 1 to 7.** Checks 9 and 10 were found by probing further after that, and are the two places it does not: anything that depends on sub-microsecond time or on byte length of Unicode text behaves like PostgreSQL, while the query still looks like T-SQL.

## Each difference

### 1. Case in comparisons
**What happens.** SQL Server's default collation is case-insensitive, so `WHERE email = 'Anshu@Example.com'` finds `anshu@example.com`. PostgreSQL's default is case-sensitive.
**Why it's silent.** The query still runs and returns rows, just fewer. A login lookup starts failing for some users; a join on codes quietly drops matches. The same applies to `LIKE`, `GROUP BY` and `DISTINCT`.
**Detect.** Count case-insensitive duplicates in key columns before migrating. **Fix.** A nondeterministic ICU collation (`und-u-ks-level2`) on the columns that relied on it. Note that pattern matching (`LIKE`) is not supported on nondeterministic collations in PostgreSQL 16, so those queries need `ILIKE` or `lower()`.

### 2 and 3. Trailing spaces
**What happens.** SQL Server pads the shorter string before comparing, so `'abc' = 'abc  '`, and `LEN` ignores trailing spaces. PostgreSQL compares `varchar` and `text` exactly and counts every character.
**Why it's silent.** Codes imported from fixed-width files often carry trailing spaces. After cutover, lookups on those codes return nothing, and length checks reject values that used to pass.
**Detect.** `WHERE col <> RTRIM(col)` on the source. **Fix.** Trim during migration, or compare with `rtrim()` explicitly. There's no setting that restores SQL Server's rule on `varchar`.

### 4. NULLs in a unique index
**What happens.** A SQL Server unique index allows one NULL. PostgreSQL treats NULLs as distinct, so a unique index allows any number.
**Why it's silent.** Nothing fails. Data the old system would have rejected now loads cleanly, and the duplicates are found much later.
**Detect.** List unique indexes on nullable columns. **Fix.** `NULLS NOT DISTINCT` on the index (PostgreSQL 15+) restores the SQL Server rule.

### 5. NULL sort order
**What happens.** SQL Server sorts NULLs first in ascending order; PostgreSQL sorts them last.
**Why it's silent.** Every row is still returned, in a different order. "Top N" queries, paging and reports change.
**Fix.** `NULLS FIRST` on the affected `ORDER BY`, or an index declared with the same ordering.

### 6. Decimal to integer
**What happens.** SQL Server truncates (`2.5` becomes 2, `-2.5` becomes -2). PostgreSQL rounds half away from zero (3 and -3).
**Why it's silent.** Quantities and amounts computed in SQL shift by one. Totals drift.
**Fix.** `trunc(x)::int` wherever SQL Server truncation was relied on.

### 7. `datetime` precision
**What happens.** SQL Server's legacy `datetime` rounds to .000, .003 or .007 seconds. PostgreSQL `timestamp` keeps microseconds.
**Why it's silent.** Equality on timestamps written by the new system no longer matches values written by the old one, and ordering of near-simultaneous events changes.
**Fix.** Use `datetime2(6)` on SQL Server and `timestamp(6)` on PostgreSQL, so both hold the same values. That's what the sample schema does, and why the copy is lossless.

### 8. Control
Both engines agree that `''` is not NULL. This check exists to prove the harness reports "same" when things are the same.

### 9. `datetime2(7)`
**What happens.** SQL Server's `datetime2` defaults to 7 fractional digits (100 ns). PostgreSQL and Babelfish keep 6, rounding the 7th.
**Why it's silent.** The copy succeeds, values move by up to half a microsecond, and equality against values kept elsewhere (an event log, another service) fails. Babelfish accepts `datetime2(7)` without a warning.
**Detect.** Find `datetime2` columns with precision 7 and check whether any value uses the 7th digit.

### 10. `DATALENGTH` of Unicode text
**What happens.** SQL Server stores `nvarchar` as UTF-16, so `DATALENGTH(N'abc')` is 6. PostgreSQL stores UTF-8, so the byte length is 3, and Babelfish reports 3 for the same T-SQL.
**Why it's silent.** Code that sizes buffers, validates payloads or estimates storage from `DATALENGTH` gets different numbers with no error.
**Detect.** Search application SQL for `DATALENGTH`.

## Noted, not tested: clustered order

SQL Server stores a table in its clustered index order, so a schema tuned around range scans on the clustered key gets them cheaply. PostgreSQL keeps no clustered order (`CLUSTER` reorders once and then drifts), so those range queries may need their own index. This changes speed, not results, so no check covers it.

## Not covered

Locking versus MVCC (readers and writers block differently), identity and sequence values after a bulk load (that one fails loudly on the next insert), and T-SQL procedure conversion.
