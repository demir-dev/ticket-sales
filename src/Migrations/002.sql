-- Preserve every existing order and ticket while allowing multiple tickets per order.
ALTER TABLE orders ADD COLUMN quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity BETWEEN 1 AND 1000);
ALTER TABLE tickets RENAME TO tickets_v1;
CREATE TABLE tickets (
 order_id TEXT NOT NULL REFERENCES orders(id),
 ordinal INTEGER NOT NULL CHECK(ordinal >= 1),
 code TEXT NOT NULL UNIQUE,
 created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
 PRIMARY KEY(order_id, ordinal)
);
INSERT INTO tickets(order_id,ordinal,code,created_at)
 SELECT order_id,1,code,created_at FROM tickets_v1;
DROP TABLE tickets_v1;
PRAGMA user_version = 2;
