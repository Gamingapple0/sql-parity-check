-- Deterministic sample data: 50 customers, 200 invoices, 600 line items.
-- Deliberately includes the awkward values a migration has to carry:
-- trailing spaces in codes, mixed-case emails, NULLs, non-ASCII names, microsecond timestamps.
WITH n AS (
    SELECT TOP (600) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
SELECT i INTO #n FROM n;

INSERT INTO customers (id, code, name, email, created_at)
SELECT i,
       CONCAT('C', RIGHT(CONCAT('000', i), 3), CASE WHEN i % 10 = 0 THEN '  ' ELSE '' END),
       CONCAT(N'Customer ', i, CASE WHEN i % 7 = 0 THEN N' Zoë Café' ELSE N'' END),
       CASE WHEN i % 5 = 0 THEN NULL
            WHEN i % 3 = 0 THEN CONCAT(N'Customer', i, N'@Example.com')
            ELSE CONCAT(N'customer', i, N'@example.com') END,
       DATEADD(microsecond, i * 1234, DATEADD(day, i, CAST('2026-01-01' AS datetime2(6))))
FROM #n WHERE i <= 50;

INSERT INTO invoices (id, customer_id, number, issued_at, due_at, total, is_paid)
SELECT i,
       i % 50 + 1,
       CONCAT('INV-', RIGHT(CONCAT('00000', i), 5)),
       DATEADD(microsecond, i * 777, DATEADD(hour, i * 5, CAST('2026-03-01' AS datetime2(6)))),
       CASE WHEN i % 4 = 0 THEN NULL
            ELSE DATEADD(day, 30, DATEADD(hour, i * 5, CAST('2026-03-01' AS datetime2(6)))) END,
       0,
       i % 2
FROM #n WHERE i <= 200;

INSERT INTO line_items (id, invoice_id, description, quantity, unit_price, amount)
SELECT i,
       i % 200 + 1,
       CONCAT(N'Item ', i),
       CAST(i % 7 + 0.5 AS decimal(10,3)),
       CAST(i % 97 + 0.99 AS decimal(12,2)),
       CAST((i % 7 + 0.5) * (i % 97 + 0.99) AS decimal(12,2))
FROM #n;

UPDATE inv SET total = s.total
FROM invoices inv
JOIN (SELECT invoice_id, SUM(amount) AS total FROM line_items GROUP BY invoice_id) s
  ON s.invoice_id = inv.id;

DROP TABLE #n;
