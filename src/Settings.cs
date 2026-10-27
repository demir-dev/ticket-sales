using System.Security.Cryptography;
using System.Text;

public sealed record Settings(string PublicUrl, string SecretKey, string WebhookSecret,
    string DatabasePath, bool AutomaticTax, bool LiveMode, Dictionary<string, string> Prices)
{
    public static Settings Load(IConfiguration config)
    {
        string Need(string name) => !string.IsNullOrWhiteSpace(config[name]) ? config[name]! :
            throw new InvalidOperationException($"Set {name} in the environment.");
        var url = Need("PUBLIC_URL").TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("PUBLIC_URL must be an HTTPS origin (HTTP localhost allowed).");
        var prices = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["standard"] = Need("STRIPE_PRICE_STANDARD"),
            ["premium"] = Need("STRIPE_PRICE_PREMIUM"),
            ["vip"] = Need("STRIPE_PRICE_VIP")
        };
        if (prices.Values.Any(x => !x.StartsWith("price_")) || prices.Values.Distinct().Count() != 3)
            throw new InvalidOperationException("Configure three distinct Stripe price_ IDs.");
        var key = Need("STRIPE_SECRET_KEY");
        var live = bool.Parse(config["STRIPE_LIVE_MODE"] ?? "false");
        if (!(key.StartsWith(live ? "sk_live_" : "sk_test_") || key.StartsWith(live ? "rk_live_" : "rk_test_")))
            throw new InvalidOperationException("Stripe key and STRIPE_LIVE_MODE do not match.");
        if (live && uri.Scheme != "https") throw new InvalidOperationException("Live mode requires HTTPS.");
        var webhook = Need("STRIPE_WEBHOOK_SECRET");
        if (!webhook.StartsWith("whsec_")) throw new InvalidOperationException("Expected a whsec_ endpoint secret.");
        return new(url, key, webhook, config["DATABASE_PATH"] ?? "data/tickets.db",
            bool.Parse(config["STRIPE_AUTOMATIC_TAX"] ?? "false"), live, prices);
    }
}

public static class Tokens
{
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string Cookie(string orderId) => "order_" + orderId;
}
