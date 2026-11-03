# Migration run

A self-managed SQL Server (a local container) migrated in two hops in ap-southeast-2: to Amazon RDS for SQL Server, then to Aurora PostgreSQL with Babelfish. Each hop is gated by `parity compare`, and both stacks are destroyed the same session.

## Plan

1. Budget alert at US$1 in place before anything is deployed.
2. `cdk diff Hop1SqlServer Hop2Babelfish -c myIp=<ip>/32`, read it: per stack one security group, one subnet group, one secret; one DB instance in `Hop1SqlServer`, one cluster plus one serverless writer and one cluster parameter group in `Hop2Babelfish`. No NAT gateway, no Multi-AZ.
3. `cdk deploy Hop1SqlServer Hop2Babelfish -c myIp=<ip>/32` (after `cdk bootstrap -c myIp=<ip>/32` once). Read endpoints and secret ARNs from the outputs; fetch each password with `aws secretsmanager get-secret-value`.
4. Create database `parity` on both targets (`CREATE DATABASE parity` through 1433), then `parity exec` `sql/schema.mssql.sql` on each. The same script serves SQL Server and Babelfish.
5. **Hop 1:** writes stopped on the source, `copy "$SRC" "$HOP1"`, then `compare "$SRC" "$HOP1"`. Exit 0 approves cutover to RDS.
6. **Hop 2:** writes stopped on RDS, `copy "$HOP1" "$HOP2"`, then `compare "$HOP1" "$HOP2"`. Exit 0 approves cutover to Aurora.
7. Read the same rows through 5432 with plain PostgreSQL, and run a few of the checks on both ports of the cluster.
8. `cdk destroy Hop1SqlServer Hop2Babelfish -c myIp=<ip>/32`, then delete the `CDKToolkit` stack and its bucket.
9. Next day: Cost Explorer, figure recorded below.

Connection strings, values from the stack outputs and secrets:

```
$SRC    = 'mssql:Server=localhost,1433;Database=parity;User ID=sa;Password=Parity!Passw0rd;TrustServerCertificate=True'
$HOP1   = 'mssql:Server=<hop1 endpoint>,1433;Database=parity;User ID=parity;Password=<secret>;TrustServerCertificate=True'
$HOP2   = 'mssql:Server=<hop2 endpoint>,1433;Database=parity;User ID=parity;Password=<secret>;TrustServerCertificate=True'
$HOP2PG = 'pg:Host=<hop2 endpoint>;Database=babelfish_db;Username=parity;Password=<secret>;SSL Mode=Require'
```

## Rehearsal: containers

The same steps against three local containers before touching AWS: SQL Server Developer as the source, SQL Server Express as the RDS stand-in, the community Babelfish image as the Aurora stand-in.

| Step | Result |
|---|---|
| Hop 1 copy | customers 50 rows (164 ms), invoices 200 (26 ms), line_items 600 (25 ms) |
| Hop 1 compare | all three tables match, exit 0 |
| One update on the source after the copy (no write freeze) | invoices hash differs, exit 1 |
| Copy again, compare | all three tables match, exit 0 |
| Hop 2 copy into Babelfish through 1433 | customers 50 (645 ms), invoices 200 (31 ms), line_items 600 (25 ms) |
| Hop 2 compare | all three tables match, exit 0 |
| Same rows through 5432 | `dbo.customers` 50, `dbo.invoices` 200, `dbo.line_items` 600 |
| No arguments | usage, exit 2 |

The update after the copy is why each hop stops writes first: without change data capture, the copy only holds what existed when it ran, and `compare` is what proves nothing changed since.

## Cloud run

| Step | Result |
|---|---|
| Deploy time, per stack | Hop1SqlServer 749 s, Hop2Babelfish 480 s (1267 s total) |
| Hop 1 copy (rows, ms per table) | customers 50 (565 ms), invoices 200 (536 ms), line_items 600 (431 ms), on the third attempt (see Notes) |
| Hop 1 compare exit code | 0, all three tables match |
| Hop 2 copy (rows, ms per table) | customers 50 (1745 ms), invoices 200 (166 ms), line_items 600 (206 ms) |
| Hop 2 compare exit code | 0, all three tables match |
| Rows seen through 5432 | `dbo.customers` 50, `dbo.invoices` 200, `dbo.line_items` 600 |
| Destroy confirmed | Both stacks destroyed, bootstrap bucket, ECR repo and `CDKToolkit` deleted; no DB instances, clusters, manual snapshots, NAT gateways or non-deleted stacks remain |
| Cost (Cost Explorer, next day) | |

Notes:
- **Hop 1 copy timed out twice on db.t3.micro.** The `insert bulk` waited on `RESOURCE_SEMAPHORE` (query memory grant): on the 1 GB instance SQL Server's memory target was about 122 MB and the grant pool about 2 MB, despite `max server memory` 725 MB. Reads (`compare`) were fine. The rehearsal did not show this because the local containers have far more memory. Fix, in `Migration.cs`: `bulk.ColumnOrderHints.Add("id", SortOrder.Ascending)` (source is already `ORDER BY id`, so no sort). Babelfish rejects the hint (`insert bulk option order is not yet supported in babelfish`, caught by the Babelfish test), so it is applied only when `@@VERSION` does not contain "Babelfish". 38 tests pass. Committed as `3aa3195`. The alternative, `db.t3.small`, was not needed.
- **No US$1 budget alert** existed (plan step 1); skipped by choice for this session.
- `cdk` was not on PATH (global npm bin dir added for the session). `cdk bootstrap` needs `-c myIp=...` because the app throws without it. This CDK version (2.1146.1) ignored `--all`, so stacks were named explicitly. `infra/cdk.context.json` (VPC lookup) is git-ignored, since it holds the account and VPC IDs.
- Behaviour checks on the same Aurora cluster: `'Anshu' = 'anshu'` is 1 on 1433 and false on 5432; `LEN('abc  ')` 3 vs `length` 5; smallest of (1, NULL) is NULL vs 1; `'' IS NULL` agrees (0 / false).
- The bootstrap bucket is versioned: `s3 rm` left versions and delete markers, so `rb` failed until they were deleted with `delete-objects`.
- `list-secrets --include-planned-deletion` returned nothing after destroy, so no scheduled-deletion secrets appeared; the expected note about scheduled deletion did not apply.
