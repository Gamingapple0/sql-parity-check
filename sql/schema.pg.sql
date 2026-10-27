-- Target schema (PostgreSQL). Same columns, same order, mapped types:
-- int -> integer, varchar/nvarchar -> varchar, datetime2(6) -> timestamp(6),
-- decimal -> numeric, bit -> boolean.
CREATE TABLE customers (
    id         integer       NOT NULL PRIMARY KEY,
    code       varchar(20)   NOT NULL,
    name       varchar(100)  NOT NULL,
    email      varchar(200)  NULL,
    created_at timestamp(6)  NOT NULL
);

CREATE TABLE invoices (
    id          integer       NOT NULL PRIMARY KEY,
    customer_id integer       NOT NULL REFERENCES customers (id),
    number      varchar(20)   NOT NULL,
    issued_at   timestamp(6)  NOT NULL,
    due_at      timestamp(6)  NULL,
    total       numeric(12,2) NOT NULL,
    is_paid     boolean       NOT NULL
);

CREATE TABLE line_items (
    id          integer       NOT NULL PRIMARY KEY,
    invoice_id  integer       NOT NULL REFERENCES invoices (id),
    description varchar(200)  NOT NULL,
    quantity    numeric(10,3) NOT NULL,
    unit_price  numeric(12,2) NOT NULL,
    amount      numeric(12,2) NOT NULL
);
