-- Source schema (SQL Server). Integer surrogate keys; codes are ordinary columns.
-- datetime2(6) matches PostgreSQL timestamp(6), so the copy is lossless.
CREATE TABLE customers (
    id         int            NOT NULL PRIMARY KEY,
    code       varchar(20)    NOT NULL,
    name       nvarchar(100)  NOT NULL,
    email      nvarchar(200)  NULL,
    created_at datetime2(6)   NOT NULL
);

CREATE TABLE invoices (
    id          int           NOT NULL PRIMARY KEY,
    customer_id int           NOT NULL REFERENCES customers (id),
    number      varchar(20)   NOT NULL,
    issued_at   datetime2(6)  NOT NULL,
    due_at      datetime2(6)  NULL,
    total       decimal(12,2) NOT NULL,
    is_paid     bit           NOT NULL
);

CREATE TABLE line_items (
    id          int            NOT NULL PRIMARY KEY,
    invoice_id  int            NOT NULL REFERENCES invoices (id),
    description nvarchar(200)  NOT NULL,
    quantity    decimal(10,3)  NOT NULL,
    unit_price  decimal(12,2)  NOT NULL,
    amount      decimal(12,2)  NOT NULL
);
