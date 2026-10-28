using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class StripeGateway(HttpClient http, Settings settings)
{
    private async Task<JsonDocument> Send(HttpMethod method, string path, Dictionary<string, string>? form = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, "https://api.stripe.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
        // Stable API contract for the small subset used here.
        request.Headers.Add("Stripe-Version", "2024-06-20");
        if (idempotencyKey != null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (form != null) request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Stripe returned HTTP {(int)response.StatusCode}.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public Task<JsonDocument> Create(Order order) => Send(HttpMethod.Post, "checkout/sessions", new()
    {
        ["submit_type"] = "book",
        ["phone_number_collection[enabled]"] = "true",
        // Email is collected by hosted Checkout by default. Collect the attendee's
        // full name separately from the cardholder name, including wallet payments.
        // Custom fields work with this app's pinned Stripe API version.
        ["custom_fields[0][key]"] = "fullname",
        ["custom_fields[0][label][type]"] = "custom",
        ["custom_fields[0][label][custom]"] = "Full name",
        ["custom_fields[0][type]"] = "text",
        ["custom_fields[0][optional]"] = "false",
        ["custom_fields[0][text][minimum_length]"] = "1",
        ["custom_fields[0][text][maximum_length]"] = "200",
        ["mode"] = "payment", ["line_items[0][price]"] = order.PriceId, ["line_items[0][quantity]"] = "1",
        ["expires_at"] = order.CreatedAt.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["client_reference_id"] = order.Id, ["metadata[order_id]"] = order.Id,
        ["success_url"] = settings.PublicUrl + "/success.html?order=" + order.Id,
        ["cancel_url"] = settings.PublicUrl + "/cancel.html?order=" + order.Id,
        ["automatic_tax[enabled]"] = settings.AutomaticTax ? "true" : "false"
    }, "ticket-" + order.Id);
    public Task<JsonDocument> Retrieve(string id) => Send(HttpMethod.Get,
        "checkout/sessions/" + Uri.EscapeDataString(id) + "?expand[]=line_items");
}

public static class StripeSignature
{
    public static bool Verify(byte[] rawBody, string header, string secret, long now)
    {
        // Multiple v1 values allow Stripe endpoint-secret rotation.
        var parts = header.Split(',').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2).ToArray();
        var timestamps = parts.Where(x => x[0] == "t").ToArray();
        if (timestamps.Length != 1 || !long.TryParse(timestamps[0][1], out var time) ||
            time < now - 300 || time > now + 300) return false;
        var prefix = Encoding.UTF8.GetBytes(timestamps[0][1] + ".");
        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed, 0); rawBody.CopyTo(signed, prefix.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        foreach (var item in parts.Where(x => x[0] == "v1"))
        {
            try { if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(item[1]))) return true; }
            catch (FormatException) { }
        }
        return false;
    }
}

public sealed class Fulfillment(Store store, StripeGateway stripe, Settings settings)
{
    public async Task Handle(JsonElement evt)
    {
        var type = evt.GetProperty("type").GetString()!;
        if (type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded" or
            "checkout.session.async_payment_failed" or "checkout.session.expired")) return;
        if (evt.GetProperty("livemode").GetBoolean() != settings.LiveMode)
            throw new InvalidOperationException("Webhook mode mismatch.");
        var source = evt.GetProperty("data").GetProperty("object");
        var reference = source.GetProperty("client_reference_id").GetString();
        // Shared Stripe accounts may deliver unrelated sessions. They do not create local orders.
        if (reference == null || store.Get(reference) is not { } order) return;
        var sessionId = source.GetProperty("id").GetString()!;
        if (order.SessionId != null && order.SessionId != sessionId) throw new InvalidOperationException("Session mismatch.");
        using var current = await stripe.Retrieve(sessionId);
        var session = current.RootElement;
        if (session.GetProperty("id").GetString() != sessionId ||
            session.GetProperty("client_reference_id").GetString() != order.Id ||
            session.GetProperty("metadata").GetProperty("order_id").GetString() != order.Id ||
            session.GetProperty("mode").GetString() != "payment" ||
            session.GetProperty("livemode").GetBoolean() != settings.LiveMode)
            throw new InvalidOperationException("Session correlation mismatch.");
        var lines = session.GetProperty("line_items");
        var items = lines.GetProperty("data");
        if (lines.GetProperty("has_more").GetBoolean() || items.GetArrayLength() != 1 ||
            items[0].GetProperty("price").GetProperty("id").GetString() != order.PriceId ||
            items[0].GetProperty("quantity").GetInt32() != 1)
            throw new InvalidOperationException("Unexpected Stripe line items.");
        // Use current Stripe state so out-of-order events cannot undo a paid order.
        var paid = session.GetProperty("payment_status").GetString() is "paid" or "no_payment_required";
        var complete = session.GetProperty("status").GetString() == "complete";
        var status = paid && complete ? "paid" : session.GetProperty("status").GetString() == "expired" ? "expired" :
            type == "checkout.session.async_payment_failed" ? "failed" : "pending";
        store.Apply(evt.GetProperty("id").GetString()!, type, order, sessionId, status,
            status == "paid" ? session.GetProperty("amount_total").GetInt64() : null,
            status == "paid" ? session.GetProperty("currency").GetString() : null);
    }
}
