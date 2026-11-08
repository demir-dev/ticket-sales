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
        if (version > 2) throw new InvalidOperationException("Database schema is newer than this app.");
        for (var next = version + 1; next <= 2; next++)
        {
            using var tx = db.BeginTransaction();
            Exec(db, tx, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", $"{next:000}.sql")));
            tx.Commit();
        }
    }
    public void Create(string id, string access, string tier, string price, int quantity = 1)
    {
        if (!TicketLimits.Valid(quantity)) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var db = Open();
        Exec(db, null, "INSERT INTO orders(id,access_hash,tier,price_id,quantity) VALUES($id,$hash,$tier,$price,$quantity)",
            ("$id", id), ("$hash", Tokens.Hash(access)), ("$tier", tier), ("$price", price), ("$quantity", quantity));
    }
    public Order? Get(string id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        Order order;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id,access_hash,tier,price_id,session_id,status,amount_total,currency,created_at,quantity FROM orders WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            order = new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetString(7), DateTimeOffset.Parse(r.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                r.GetInt32(9), []);
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT code FROM tickets WHERE order_id=$id ORDER BY ordinal";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            var codes = new List<string>();
            while (r.Read()) codes.Add(r.GetString(0));
            order = order with { Tickets = codes.ToArray() };
        }
        tx.Commit();
        return order;
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
        if (status == "paid")
            for (var ordinal = 1; ordinal <= order.Quantity; ordinal++)
                Exec(db, tx, "INSERT INTO tickets(order_id,ordinal,code) VALUES($id,$ordinal,$code) ON CONFLICT(order_id,ordinal) DO NOTHING",
                    ("$id", order.Id), ("$ordinal", ordinal), ("$code", Tokens.New()));
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
    string Status, long? AmountTotal, string? Currency, DateTimeOffset CreatedAt, int Quantity, string[] Tickets)
{
    public string? Ticket => Tickets.FirstOrDefault();
}
public static class TicketLimits
{
    public const int Maximum = 1000;
    public static bool Valid(int quantity) => quantity >= 1 && quantity <= Maximum;
}
