using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var count = 0;
void Assert(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
var raw = Encoding.UTF8.GetBytes("{\"a\":1}");
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
const string secret = "whsec_test_only";
string Signature(long t) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + ".").Concat(raw).ToArray()));
var header = $"t={now},v1={Signature(now)}";
Assert(StripeSignature.Verify(raw, header, secret, now), "valid raw-body signature");
Assert(!StripeSignature.Verify(Encoding.UTF8.GetBytes("{ \"a\":1}"), header, secret, now), "body mutation rejected");
Assert(!StripeSignature.Verify(raw, header, "whsec_wrong", now), "wrong endpoint secret rejected");
Assert(!StripeSignature.Verify(raw, $"t={now-301},v1={Signature(now-301)}", secret, now), "old signature rejected");
Assert(!StripeSignature.Verify(raw, $"t={now+301},v1={Signature(now+301)}", secret, now), "future signature rejected");
Assert(StripeSignature.Verify(raw, $"t={now},v1=00,v1={Signature(now)}", secret, now), "rotating signatures supported");
Assert(!StripeSignature.Verify(raw, "t=invalid,v1=zz", secret, now), "malformed signature rejected");

var directory = Path.Combine(Path.GetTempPath(), "ticket-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var store = new Store(Path.Combine(directory, "test.db")); store.Initialize(); store.Initialize();
    var settings = new Settings("http://localhost:5080", "sk_test_placeholder", secret, "unused", false, false,
        new() { ["standard"] = "price_standard", ["premium"] = "price_premium", ["vip"] = "price_vip" });
    var fake = new FakeStripe();
    var stripe = new StripeGateway(new HttpClient(fake), settings);
    var handler = new Fulfillment(store, stripe, settings);
    var orderId = Guid.NewGuid().ToString("N");
    store.Create(orderId, "access", "standard", "price_standard", 3);
    JsonDocument Event(string id, string type, string? reference = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
    { id, type, livemode = false, data = new { @object = new { id = "cs_test_123", client_reference_id = reference ?? orderId } } }));
    void Session(string status, string payment, string price = "price_standard", string? reference = null, int quantity = 3) => fake.Json = JsonSerializer.Serialize(new
    {
        id = "cs_test_123", client_reference_id = reference ?? orderId, metadata = new { order_id = orderId },
        mode = "payment", livemode = false, status, payment_status = payment, amount_total = 1200, currency = "eur",
        line_items = new { has_more = false, data = new[] { new { price = new { id = price }, quantity } } }
    });
    async Task Deliver(string id, string type) { using var evt = Event(id, type); await handler.Handle(evt.RootElement); }
    Session("complete", "unpaid");
    await Deliver("evt_pending", "checkout.session.completed");
    Assert(store.Get(orderId)!.Ticket == null && store.Get(orderId)!.Status == "pending", "delayed payment creates no ticket");
    await Deliver("evt_failed", "checkout.session.async_payment_failed");
    Assert(store.Get(orderId)!.Status == "failed" && store.Get(orderId)!.Ticket == null, "delayed payment failure records failure without ticket");
    await Deliver("evt_old_completion", "checkout.session.completed");
    Assert(store.Get(orderId)!.Status == "failed", "old unpaid completion does not undo failure");
    Session("expired", "unpaid");
    await Deliver("evt_expired", "checkout.session.expired");
    Assert(store.Get(orderId)!.Status == "expired" && store.Get(orderId)!.Ticket == null, "expiration issues no ticket");
    Session("complete", "paid", quantity: 2);
    var quantityRejected = false;
    try { await Deliver("evt_quantity", "checkout.session.async_payment_succeeded"); } catch (InvalidOperationException) { quantityRejected = true; }
    Assert(quantityRejected && store.Get(orderId)!.Tickets.Length == 0, "mismatched paid Stripe quantity cannot issue tickets");
    Session("complete", "paid", "price_wrong");
    var rejected = false;
    try { await Deliver("evt_retry", "checkout.session.async_payment_succeeded"); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected && store.Get(orderId)!.Ticket == null, "wrong Stripe price rejected");
    Session("complete", "paid", reference: "other_order");
    rejected = false;
    try { await Deliver("evt_retry", "checkout.session.async_payment_succeeded"); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "wrong local correlation rejected");
    Session("complete", "paid");
    fake.Fail = true;
    rejected = false;
    try { await Deliver("evt_retry", "checkout.session.async_payment_succeeded"); } catch (HttpRequestException) { rejected = true; }
    Assert(rejected && store.Get(orderId)!.Ticket == null, "Stripe outage leaves event retryable");
    fake.Fail = false;
    await Deliver("evt_retry", "checkout.session.async_payment_succeeded");
    var ticket = store.Get(orderId)!.Ticket;
    Assert(store.Get(orderId)!.Tickets.Length == 3 && store.Get(orderId)!.Tickets.Distinct().Count() == 3,
        "three paid admissions receive three distinct ticket codes");
    Assert(ticket?.Length == 64 && store.Get(orderId)!.AmountTotal == 1200, "paid session issues unique ticket and amount");
    await Deliver("evt_retry", "checkout.session.async_payment_succeeded");
    await Deliver("evt_second", "checkout.session.completed");
    Assert(store.Get(orderId)!.Ticket == ticket, "duplicate event and duplicate session fulfillment are idempotent");
    await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Deliver("evt_concurrent_" + i, "checkout.session.completed"))));
    Assert(store.Get(orderId)!.Ticket == ticket, "concurrent fulfillment preserves a single ticket");
    Session("complete", "unpaid");
    await Deliver("evt_stale", "checkout.session.async_payment_failed");
    Assert(store.Get(orderId)!.Status == "paid" && store.Get(orderId)!.Ticket == ticket, "late failure cannot downgrade paid order");
    using (var db = store.Open())
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM tickets";
        Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 3, "exactly three unique tickets despite retries and concurrency");
    }
    using (var unrelated = Event("evt_unknown", "checkout.session.completed", "unknown")) await handler.Handle(unrelated.RootElement);
    Assert(store.Get("unknown") == null, "unrelated Stripe sessions do not create orders");
    // Exercise rollback after the event insert: a session conflict must not consume the event ID.
    var other = Guid.NewGuid().ToString("N"); store.Create(other, "access", "vip", "price_vip");
    rejected = false;
    try { store.Apply("evt_rollback", "test", store.Get(other)!, "cs_test_123", "paid", 1, "eur"); }
    catch (Microsoft.Data.Sqlite.SqliteException) { rejected = true; }
    store.Apply("evt_rollback", "test", store.Get(other)!, "cs_other", "paid", 1, "eur");
    Assert(rejected && store.Get(other)!.Ticket != null, "event, order and ticket roll back together");
    // Retry request uses stable Stripe idempotency key and only server-owned price/return URLs.
    fake.Json = "{\"id\":\"cs_new\",\"url\":\"https://checkout.stripe.com/c/pay/test\"}";
    using var created = await stripe.Create(store.Get(orderId)!);
    Assert(fake.IdempotencyKey == "ticket-" + orderId && fake.Form.Contains("price_standard") && !fake.Form.Contains("price_wrong"), "server-side price and stable Stripe idempotency key");
    var form = fake.Form.Split('&').Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1].Replace("+", " ")));
    Assert(form["submit_type"] == "book", "Checkout uses Book action");
    Assert(form["phone_number_collection[enabled]"] == "true", "Checkout collects phone number");
    Assert(form["custom_fields[0][key]"] == "fullname" && form["custom_fields[0][label][custom]"] == "Full name" &&
        form["custom_fields[0][type]"] == "text" && form["custom_fields[0][optional]"] == "false",
        "Checkout requires full name independently of cardholder name");
    Assert(form["line_items[0][quantity]"] == "3", "Stripe receives server-stored quantity");
    Assert(!TicketLimits.Valid(0) && !TicketLimits.Valid(-1) && !TicketLimits.Valid(1001) && TicketLimits.Valid(1) && TicketLimits.Valid(1000),
        "quantity bounds reject invalid orders");
    var batchId = Guid.NewGuid().ToString("N");
    store.Create(batchId, "access", "premium", "price_premium", 3);
    using (var db = store.Open()) Store.Exec(db, null,
        "CREATE TRIGGER fail_batch BEFORE INSERT ON tickets WHEN NEW.ordinal=2 BEGIN SELECT RAISE(ABORT,'test failure'); END;");
    rejected = false;
    try { store.Apply("evt_batch", "test", store.Get(batchId)!, "cs_batch", "paid", 300, "eur"); }
    catch (Microsoft.Data.Sqlite.SqliteException) { rejected = true; }
    Assert(rejected && store.Get(batchId)!.Status == "pending" && store.Get(batchId)!.Tickets.Length == 0,
        "failure halfway through ticket issuance rolls back entire batch and paid state");
    using (var db = store.Open()) Store.Exec(db, null, "DROP TRIGGER fail_batch");
    store.Apply("evt_batch", "test", store.Get(batchId)!, "cs_batch", "paid", 300, "eur");
    Assert(store.Get(batchId)!.Tickets.Length == 3, "failed batch event can retry and issue all tickets");
    var legacy = new Store(Path.Combine(directory, "legacy.db"));
    using (var db = legacy.Open())
    {
        Store.Exec(db, null, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "001.sql")));
        Store.Exec(db, null, "INSERT INTO orders(id,access_hash,tier,price_id,status) VALUES('old','hash','standard','price_old','paid')");
        Store.Exec(db, null, "INSERT INTO tickets(order_id,code) VALUES('old','original_code')");
        Store.Exec(db, null, "INSERT INTO stripe_events(id,type) VALUES('evt_old','test')");
    }
    legacy.Initialize(); legacy.Initialize();
    Assert(legacy.Get("old")!.Quantity == 1 && legacy.Get("old")!.Ticket == "original_code",
        "schema migration preserves existing paid tickets and defaults quantity to one");
    using (var db = legacy.Open())
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM stripe_events WHERE id='evt_old'";
        Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 1, "migration preserves processed event history");
    }
    Console.WriteLine($"All {count} checks passed.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

sealed class FakeStripe : HttpMessageHandler
{
    public string Json { get; set; } = "{}";
    public bool Fail { get; set; }
    public string? IdempotencyKey { get; private set; }
    public string Form { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "api.stripe.com") throw new Exception("Unexpected API host.");
        IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
        Form = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(Json) };
    }
}
