# Static selector verification

- Home page has three direct links to the supplied Stripe Payment Links.
- No quantity input, customer form, JavaScript, or checkout API call on the home page.
- Docker image serves only index.html and style.css; legacy backend and order pages are excluded.
- No Stripe secrets or database settings required.

Deployment checks: `docker compose config --quiet`, `docker compose build`, then `curl --fail http://127.0.0.1:8087/health` after starting the container. Verify all three ticket links in a browser.
