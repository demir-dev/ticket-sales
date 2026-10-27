using Microsoft.Data.Sqlite;

public sealed class Store(string path)
{
    public SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, DefaultTimeout = 15 }.ToString());
        db.Open();
        return db;
    }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = Open();
        Exec(db, null, "PRAGMA journal_mode=WAL;");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 1) throw new InvalidOperationException("Database schema is newer than this app.");
        if (version == 0)
        {
            using var tx = db.BeginTransaction();
            Exec(db, tx, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "001.sql")));
            tx.Commit();
        }
    }
    public void Create(string id, string access, string tier, string price)
    {
        using var db = Open();
        Exec(db, null, "INSERT INTO orders(id,access_hash,tier,price_id) VALUES($id,$hash,$tier,$price)",
            ("$id", id), ("$hash", Tokens.Hash(access)), ("$tier", tier), ("$price", price));
    }
    public Order? Get(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT o.id,access_hash,tier,price_id,session_id,status,amount_total,currency,t.code,o.created_at FROM orders o LEFT JOIN tickets t ON t.order_id=o.id WHERE o.id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return !r.Read() ? null : new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), DateTimeOffset.Parse(r.GetString(9), System.Globalization.CultureInfo.InvariantCulture));
    }
    public void BindSession(string id, string session)
    {
        using var db = Open();
        if (Exec(db, null, "UPDATE orders SET session_id=$session WHERE id=$id AND (session_id IS NULL OR session_id=$session)",
            ("$session", session), ("$id", id)) != 1) throw new InvalidOperationException("Session mismatch.");
    }
    public void Apply(string eventId, string type, Order order, string session, string status, long? amount, string? currency)
    {
        using var db = Open();
        // Immediate transaction serializes competing deliveries; event and ticket commit together.
        using var tx = db.BeginTransaction(deferred: false);
        if (Exec(db, tx, "INSERT OR IGNORE INTO stripe_events(id,type) VALUES($id,$type)",
            ("$id", eventId), ("$type", type)) == 0) { tx.Commit(); return; }
        if (Exec(db, tx, """
            UPDATE orders SET session_id=$session,
                status=CASE WHEN status='paid' THEN 'paid' WHEN $status='pending' AND status IN ('failed','expired') THEN status ELSE $status END,
                amount_total=CASE WHEN $status='paid' THEN $amount ELSE amount_total END,
                currency=CASE WHEN $status='paid' THEN $currency ELSE currency END,
                paid_at=CASE WHEN $status='paid' THEN COALESCE(paid_at,strftime('%Y-%m-%dT%H:%M:%fZ','now')) ELSE paid_at END
            WHERE id=$id AND (session_id IS NULL OR session_id=$session)
            """, ("$id", order.Id), ("$session", session), ("$status", status),
            ("$amount", amount), ("$currency", currency)) != 1) throw new InvalidOperationException("Session mismatch.");
        if (status == "paid") Exec(db, tx, "INSERT OR IGNORE INTO tickets(order_id,code) VALUES($id,$code)",
            ("$id", order.Id), ("$code", Tokens.New()));
        tx.Commit();
    }
    public static int Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
public sealed record Order(string Id, string AccessHash, string Tier, string PriceId, string? SessionId,
    string Status, long? AmountTotal, string? Currency, string? Ticket, DateTimeOffset CreatedAt);
