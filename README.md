# sql-parity-check

Same data, same queries, two databases: where do SQL Server and PostgreSQL silently give different answers?

A migration can move every row correctly and still change behaviour. No errors, no failed jobs, just a query that returns something different after cutover. This repo loads identical data into both engines and flags those differences.

## Status

Skeleton only. Nothing runs yet.

## Planned layout

| Path | Purpose |
|---|---|
| `sql/` | Schema and seed data, one script per engine |
| `checks/` | One check per behavioural difference: the query, run on both engines, compared |
| `DIFFERENCES.md` | Each difference: what happens, why it's silent, how to detect it |

## Planned approach

1. Run SQL Server and PostgreSQL side by side in Docker.
2. Load the same invoice-style dataset into both.
3. Run each check against both engines and report mismatches.
4. Add row counts and per-table checksums as the baseline parity check.

## Decisions still open

- Script language for the checks
- Docker images and versions to pin
- Dataset shape and size


