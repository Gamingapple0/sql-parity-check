# Silent differences: SQL Server vs PostgreSQL

Each entry gets: what happens, why nothing errors, and how a check detects it. To be verified by running both engines before anything is written as fact.

## Candidates

- [ ] String comparison and case sensitivity (default collation)
- [ ] Trailing spaces in string equality
- [ ] NULLs in unique constraints
- [ ] Datetime precision and rounding
- [ ] Time zone handling
- [ ] Integer division and numeric rounding
- [ ] Empty string versus NULL
- [ ] Default sort order of strings and NULLs
