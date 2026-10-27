# Verification

Verified during project creation:

- Release build/publish succeeded with warnings treated as errors.
- All 22 .NET security/payment/database checks passed.
- HTTP smoke checks passed against both the build output and the published app, including static UI, response headers, cookie-protected order access, forged return URL, Origin check, price allowlist, raw-body signature validation, and unrelated Stripe event handling.
- Docker Compose configuration validated using placeholder environment values.

Not exercised here:

- Docker image build/container launch: the local Docker daemon was not running.
- Live or test-mode Stripe API calls and real signed Stripe deliveries: no actual Stripe credentials or price IDs were supplied. Follow the README's test-mode checklist before live use.
- DNS and public HTTPS provisioning: these require your server/subdomain.

No production deployment or external payment was performed.
