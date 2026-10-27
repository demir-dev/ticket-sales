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
    store.Create(orderId, "access", "standard", "price_standard");
    JsonDocument Event(string id, string type, string? reference = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
    { id, type, livemode = false, data = new { @object = new { id = "cs_test_123", client_reference_id = reference ?? orderId } } }));
    void Session(string status, string payment, string price = "price_standard", string? reference = null) => fake.Json = JsonSerializer.Serialize(new
    {
        id = "cs_test_123", client_reference_id = reference ?? orderId, metadata = new { order_id = orderId },
        mode = "payment", livemode = false, status, payment_status = payment, amount_total = 1200, currency = "eur",
        line_items = new { has_more = false, data = new[] { new { price = new { id = price }, quantity = 1 } } }
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
        Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 1, "one ticket per order enforced in database");
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
