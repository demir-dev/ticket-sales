# Ticket selector

Static ticket page with direct Stripe Payment Links. Clicking a ticket opens Stripe; quantity, customer details, payment methods, and confirmation are configured on the Payment Link in Stripe. No API keys, database, webhooks, or local ticket generation are required.

| Ticket | Payment Link |
| --- | --- |
| Standard | https://book.stripe.com/dRmeVdb28aBh6Lq1My7Vm0c |
| Premium | https://buy.stripe.com/14AeVdfio8t9b1Gbn87Vm0d |
| VIP | https://buy.stripe.com/14A4gz8U0gZF4Di4YK7Vm0e |

Edit displayed names, prices, perks, and links in `src/wwwroot/index.html`. Keep displayed prices aligned with Stripe. Checkout options are controlled in the Stripe dashboard.

## Deployment

Docker Compose serves only `index.html` and `style.css` using Caddy on container port 8080. The host Caddy service provides public HTTPS. Legacy .NET source remains in the repository but is not built or run by Docker.

On the server, inside the repository:

```sh
git pull --ff-only
cp .env.example .env
docker compose up -d --build --remove-orphans
docker compose ps
curl --fail http://127.0.0.1:8087/health
```

The `.env` file contains only `APP_PORT=8087`. It is ignored by Git. No Stripe secrets are used. The previous database volume is left intact; do not use `down -v` if you want to retain old data.

Merge the site block in this repository's `Caddyfile` into `/etc/caddy/Caddyfile` on the host, preserving other sites. Then:

```sh
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
curl --fail --head https://paok-arena-tickets.morphiclabs.net
```

The domain's DNS must point to this server, with ports 80 and 443 open. If changing `APP_PORT`, update the host Caddy proxy port to match. Host Caddy must run on the host for the loopback proxy address to work.

## Checks

```sh
docker compose config --quiet
docker compose build
```

Open the site and check that each ticket navigates to its intended Stripe link. The selector also works with JavaScript disabled. Existing .NET tests concern only the legacy backend.
