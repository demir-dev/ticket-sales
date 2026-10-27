using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 262144);
var settings = Settings.Load(builder.Configuration);
var store = new Store(settings.DatabasePath); store.Initialize();
builder.Services.AddSingleton(settings); builder.Services.AddSingleton(store);
builder.Services.AddHttpClient<StripeGateway>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddTransient<Fulfillment>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Global limits are deliberate: deployment does not trust spoofable forwarded IP headers.
    o.AddFixedWindowLimiter("checkout", x => { x.PermitLimit = 30; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; });
    o.AddFixedWindowLimiter("status", x => { x.PermitLimit = 600; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (settings.PublicUrl.StartsWith("https:")) context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
    try { await next(); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed");
        if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Temporarily unavailable. Please retry." }); }
    }
});
app.UseRateLimiter();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/checkout", async (HttpContext context, StripeGateway stripe) =>
{
    if (context.Request.Headers.Origin.ToString() != settings.PublicUrl) return Results.StatusCode(403);
    if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
    CheckoutInput? input;
    try { input = await context.Request.ReadFromJsonAsync<CheckoutInput>(); }
    catch (JsonException) { return Results.BadRequest(); }
    if (input?.Tier == null || !settings.Prices.TryGetValue(input.Tier, out var price)) return Results.BadRequest(new { error = "Choose a valid ticket." });
    // A retry uses the same local order and Stripe idempotency key. Tier changes create a new order.
    Order order;
    if (input.OrderId != null && store.Get(input.OrderId) is { } existing && Authorized(context, existing) && existing.Tier == input.Tier && existing.Status == "pending") order = existing;
    else
    {
        var id = Guid.NewGuid().ToString("N"); var access = Tokens.New();
        store.Create(id, access, input.Tier, price); order = store.Get(id)!;
        context.Response.Cookies.Append(Tokens.Cookie(id), access, new CookieOptions
        {
            HttpOnly = true, Secure = settings.PublicUrl.StartsWith("https:"), SameSite = SameSiteMode.Lax,
            Path = "/api", MaxAge = TimeSpan.FromDays(7), IsEssential = true
        });
    }
    // Return order id even on Stripe failure so the browser can retry without another session.
    try
    {
        if (order.CreatedAt < DateTimeOffset.UtcNow.AddHours(-20))
            return Results.Json(new { error = "This checkout is too old to resume. Check its payment status before starting a new order.", orderId = order.Id }, statusCode: 409);
        using var session = order.SessionId == null ? await stripe.Create(order) : await stripe.Retrieve(order.SessionId);
        if (order.SessionId != null && session.RootElement.GetProperty("status").GetString() != "open")
            return Results.Json(new { error = "Checkout is closed. Check the order status using the link below.", orderId = order.Id }, statusCode: 409);
        var id = session.RootElement.GetProperty("id").GetString()!;
        var url = session.RootElement.GetProperty("url").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "checkout.stripe.com")
            throw new InvalidOperationException("Unexpected Checkout URL.");
        store.BindSession(order.Id, id);
        return Results.Ok(new { orderId = order.Id, url });
    }
    catch (HttpRequestException)
    { return Results.Json(new { error = "Stripe is unavailable. Please retry.", orderId = order.Id }, statusCode: 503); }
}).RequireRateLimiting("checkout");
app.MapGet("/api/orders/{id}", (string id, HttpContext context) =>
{
    var order = store.Get(id);
    if (order == null || !Authorized(context, order)) return Results.NotFound();
    return Results.Ok(new { order.Id, order.Tier, order.Status, order.AmountTotal, order.Currency, ticket = order.Ticket });
}).RequireRateLimiting("status");
app.MapPost("/stripe/webhook", async (HttpContext context, Fulfillment fulfillment) =>
{
    using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body);
    var raw = body.ToArray();
    if (!StripeSignature.Verify(raw, context.Request.Headers["Stripe-Signature"].ToString(), settings.WebhookSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        return Results.BadRequest();
    try
    {
        using var evt = JsonDocument.Parse(raw);
        await fulfillment.Handle(evt.RootElement);
        return Results.Ok();
    }
    catch (JsonException) { return Results.BadRequest(); }
});
app.Run();

static bool Authorized(HttpContext context, Order order)
{
    var token = context.Request.Cookies[Tokens.Cookie(order.Id)];
    return token != null && token.Length == 64 && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(Tokens.Hash(token)), Encoding.UTF8.GetBytes(order.AccessHash));
}
public sealed record CheckoutInput(string? Tier, string? OrderId);
