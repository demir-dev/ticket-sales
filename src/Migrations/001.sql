CREATE TABLE IF NOT EXISTS orders (
 id TEXT PRIMARY KEY,
 access_hash TEXT NOT NULL,
 tier TEXT NOT NULL,
 price_id TEXT NOT NULL,
 session_id TEXT UNIQUE,
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','failed','expired','paid')),
 amount_total INTEGER,
 currency TEXT,
 created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
 paid_at TEXT
);
CREATE TABLE IF NOT EXISTS tickets (
 order_id TEXT PRIMARY KEY REFERENCES orders(id),
 code TEXT NOT NULL UNIQUE,
 created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE TABLE IF NOT EXISTS stripe_events (
 id TEXT PRIMARY KEY,
 type TEXT NOT NULL,
 processed_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
PRAGMA user_version = 1;
