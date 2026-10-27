# Minimal ticket sales

ASP.NET Core 10 + SQLite, plain HTML/CSS/JS, Stripe-hosted Checkout, Docker and Caddy HTTPS. One ticket per checkout; Standard/Premium/VIP map to your three existing Stripe **Price IDs**. No frontend framework, account system, queue, or ORM. No live secrets are included.

## What you need

- .NET 10 SDK for local use, or Docker Engine + Compose on a server.
- Three active **one-time** Stripe prices (`price_...`), one for each tier. Open each existing product in Stripe and copy its Price ID; product IDs (`prod_...`) and Payment Link URLs are not price IDs. Use test-mode prices with test keys; live-mode prices with live keys. All three should use your intended currency.
- A Stripe secret API key and webhook **endpoint signing secret** (`whsec_...`). These are different secrets. No publishable key is needed because checkout is hosted by Stripe.
- A subdomain whose DNS points to your server. Set up the displayed event name, tier descriptions, and relevant purchase terms in `src/wwwroot/index.html` before selling.

## Local setup

```sh
cp .env.example .env
# Edit .env: PUBLIC_URL=http://localhost:5080, real TEST price IDs and TEST key.
# DOMAIN is only used by Compose/Caddy.
```

The app reads environment variables, not `.env` automatically. For local development with the supplied simple values, export them from your trusted `.env` file:

```sh
set -a
. ./.env
set +a
stripe listen --forward-to http://localhost:5080/stripe/webhook
```

Copy the CLI's `whsec_...` into `.env`. In a second terminal, export `.env` again and start:

```sh
set -a
. ./.env
set +a
dotnet run --project src/TicketSales.csproj --urls http://localhost:5080
```

Open `http://localhost:5080`. Keep the hostname consistent: `localhost` and `127.0.0.1` have different origins/cookies. This `.env` loading command executes shell syntax; only use your own trusted file. SQLite is created at `data/tickets.db`, relative to the app working directory. `DATABASE_PATH` can be absolute.

Use Stripe test card `4242 4242 4242 4242`, a future expiration, and any three-digit CVC. No real payment is made with test credentials. The CLI signing secret is for CLI-forwarded events; it is not the production endpoint secret.

## Deploy on your subdomain

1. Point the subdomain's A/AAAA DNS records to the server (remove stale AAAA records). Permit inbound TCP 80/443 and UDP 443 if HTTP/3 is desired.
2. Copy this folder to the server. Copy `.env.example` to `.env`, set `DOMAIN=tickets.yourdomain.com`, `PUBLIC_URL=https://tickets.yourdomain.com`, and your Stripe configuration. `PUBLIC_URL` is an origin with no path. Keep `.env` private (`chmod 600 .env`); do not put secrets in source control.
3. Register `https://tickets.yourdomain.com/stripe/webhook` in Stripe Workbench/Dashboard as a **snapshot** webhook endpoint for your account, not a thin event or Connect endpoint. Subscribe to:
   - `checkout.session.completed`
   - `checkout.session.async_payment_succeeded`
   - `checkout.session.async_payment_failed`
   - `checkout.session.expired`
4. Put that endpoint's signing secret in `.env`. Start with test mode. For production switch to live prices/key/endpoint secret and set `STRIPE_LIVE_MODE=true` together.
5. Run:

```sh
docker compose up -d --build
docker compose logs --tail=100 app caddy
```

Caddy obtains and renews HTTPS certificates. The app has no published host port; only Caddy is public. No forwarded headers are trusted, and return URLs come exclusively from `PUBLIC_URL`. After configuration changes use `docker compose up -d --build` to recreate the app. `docker compose down` preserves the database volume; **`down -v` deletes it**.

The container runs as the built-in unprivileged `app` user. A new named database volume inherits `/data` ownership from the image. If substituting a bind mount, set its ownership to the image's app UID (normally 1654). Run **one app replica on one server**; this SQLite setup is not for shared/network storage or multi-host deployments.

The base images use supported .NET 10 and Caddy 2 tags to receive patches on rebuild. Pull and rebuild periodically (`docker compose build --pull`). API calls pin `Stripe-Version: 2024-06-20` for the fields this app uses; webhook processing reads raw JSON and retrieves the current Session through that pinned API.

## Payment and security flow

1. The browser POSTs a tier name, never an amount or arbitrary price. The server selects its configured price, creates an order, and sets a random per-order HttpOnly, SameSite cookie (Secure over HTTPS, seven-day lifetime). Only a SHA-256 hash of that cookie is stored.
2. The server creates a Checkout Session with quantity 1, its own return URLs, `client_reference_id` and metadata containing the local order ID. The local order ID is also the Stripe idempotency key. Sessions expire after one hour. A bound session is retrieved when retrying checkout, and closed sessions are not recreated. Unbound retries use the original expiration; old orders are refused before Stripe's idempotency retention window can elapse.
3. Stripe POSTs a webhook. The handler verifies HMAC-SHA256 over the **original body bytes** using the endpoint secret, constant-time comparison, and a five-minute timestamp tolerance; it supports multiple v1 signatures during secret rotation. Keep the server clock synchronized. No body parsing happens before verification.
4. The handler checks test/live mode, looks up an existing local order, retrieves the current Session from Stripe, and validates the order reference, metadata, mode, exact stored price, and quantity. It issues a ticket only for a complete Session whose `payment_status` is `paid` or `no_payment_required` (for zero-cost Stripe prices).
5. A SQLite immediate transaction inserts the unique event ID, changes the order, and inserts a unique ticket together. Unique Session ID and one ticket per order protect against duplicate event IDs, different events for the same session, concurrent deliveries, and retries. Paid orders cannot be downgraded by late failure/expiration events. Failures roll back the event record so Stripe can retry; temporary errors return 503.
6. An unpaid `checkout.session.completed` leaves the order pending. `async_payment_succeeded` can fulfill it later; `async_payment_failed` records failure. The cancel page changes no database state. The success page polls only the database; a forged return URL cannot issue a ticket. It requires the order's private cookie even if the order ID is known.

The app does not store card details or customer email. Request bodies, cookies and Stripe credentials are not logged by this code. Response headers prohibit framing, caching, third-party scripts and referrer leakage. Checkout POSTs require the configured Origin and JSON. Simple global rate limits cap session creation and status polling without trusting client-supplied IPs; tune the limits for your event's traffic. Webhooks are not rate limited so Stripe retries remain deliverable.

### What about existing Payment Links?

A **redirect-only** Payment Link callback is insufficient payment proof and does not, by itself, establish a trustworthy association with a previously created local order. Payment Links can be integrated securely with verified webhooks and deliberate correlation (for example a server-issued `client_reference_id`), but a browser-supplied callback “secret” is not a replacement for that verification. This app uses server-created Checkout Sessions to attach the local order and allowlisted price directly. Your existing Stripe products/prices are reused; existing Payment Link URLs are not needed.

### Tax

Set `STRIPE_AUTOMATIC_TAX=true` only after configuring Stripe Tax, appropriate registrations, product tax codes and price tax behavior in your Stripe account. Stripe Checkout displays the final price/tax. This switch does not establish tax registrations. The app records Stripe's final `amount_total` in the currency's smallest unit; it does not calculate tax or assume every currency uses cents. Taxes are off by default.

## Database and backups

`src/Migrations/001.sql` defines orders, tickets, event deduplication and schema version. The app applies it transactionally on startup using SQLite `user_version`. It refuses newer schemas. All SQL values are parameterized. Keep backups of the persistent `tickets` volume; the DB is the source of issued tickets and processed events.

For the simplest consistent backup, stop the app briefly, back up the **entire volume** (including any WAL/SHM files), then restart it. Do not copy just a live `.db` file while writes are active. Restore a backup into a stopped app's volume with correct ownership, then start the app. Stripe retries missed deliveries for a limited period; inspect failures in Stripe and resend missed events after an outage or restore. Do not delete processed event IDs casually.

## Tests

Run without any Stripe secrets or external payment requests:

```sh
dotnet restore tests/TicketSales.Tests.csproj
dotnet run --project tests/TicketSales.Tests.csproj -c Release --no-restore
python3 tests/http_smoke.py
```

The .NET executable is a dependency-light test harness that exits nonzero on failure. It uses a fake HTTP handler only in tests; production has no Stripe-host override or bypass. Tests cover original-body signature verification, secret/timestamp rejection, secret rotation, unpaid sessions, price/reference mismatch, Stripe failures and retry, repeated fulfillment, failure after payment, one-ticket constraints, atomic rollback, and server-created session idempotency. Optionally pass a published app directory to the HTTP test (`python3 tests/http_smoke.py /absolute/path/to/publish`) to verify the deployable files, including migrations and static assets. The Python test starts the built app on a temporary local port with a temporary database and tests the actual HTTP boundaries.

Before live sales, run this manual test-mode checklist:

- Complete a checkout from each tier. Confirm each uses the intended Stripe price and produces one DB order and ticket.
- Resend the same event in Stripe or `stripe events resend evt_... --webhook-endpoint=we_...` for a registered endpoint. Confirm the ticket code remains unchanged.
- Follow the cancel link. Confirm it doesn't issue a ticket. Manually open `/success.html?order=anything`; no ticket should appear.
- If enabled in your Stripe account, test a delayed payment method: completion while unpaid must show pending; success issues a ticket; failure does not. Use Stripe's current test instructions for the enabled payment method. Enable only payment methods appropriate for your event's lead time.
- Temporarily stop the app during a payment, start it, and resend the event. Fulfillment must recover once.
- Restart the app/containers and confirm the saved ticket still appears in the original browser.

`stripe trigger checkout.session.completed` usually creates an unrelated fixture without this app's local order reference; it is intentionally ignored. Use a real **test-mode checkout created from this app** to test end to end.

## Deliberate limits

- One ticket per checkout. No inventory cap, seat reservations, admission scanner/check-in, email delivery, customer accounts, refund/dispute automation or administrative UI. Verify codes against the database manually; codes are 256-bit random bearer credentials. QR images are not included. Do not treat possession of a screenshot as proof that a ticket has not already been used. Add authenticated, atomic redemption if you need gate scanning.
- Buyers should save/print the ticket. Access to the order page lasts seven days in the original browser; a lost/cleared cookie requires manual operator recovery using Stripe's order reference and database. There is no email recovery. The printable ticket code remains in the database after the cookie expires.
- Payments made by separate intentional new checkouts can each issue a ticket. Session retries reuse the same order where the browser keeps its checkout state; this is not a cart or cross-device deduplication system. Avoid restarting checkout while a delayed payment is pending.
- Refunds/disputes do **not** automatically invalidate issued tickets in this small version. Handle cancellations manually and add revocation/check-in before using it for events needing those controls. There is no automatic stock enforcement, so do not use it for capacity-limited sales without adding an inventory reservation policy.
- Existing orders retain their original price IDs when you change configuration. Existing Sessions may still complete. Existing unrelated Stripe sessions never create orders.

## Stripe references

- [Create Checkout Sessions](https://docs.stripe.com/api/checkout/sessions/create)
- [Checkout fulfillment](https://docs.stripe.com/checkout/fulfillment)
- [Webhook signatures and delivery recovery](https://docs.stripe.com/events/manage-webhook-endpoints)
