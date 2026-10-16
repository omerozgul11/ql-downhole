# Deploying the licensing server

## Requirements

* A Linux host with Docker Engine and Docker Compose v2.
* A DNS name (e.g. `licensing.quakelogic.net`) and a TLS-terminating reverse proxy (nginx, Caddy,
  Traefik or a Cloudflare tunnel) in front of the licence server.
* The admin portal must not be public: expose it only through a VPN or an authenticated proxy. It
  refuses plain-HTTP requests.
* An SMTP account for transactional e-mail and, for online sales, a Stripe account.

## Steps

```bash
cd server
cp .env.example .env                    # fill in POSTGRES_PASSWORD, LICENSING_KEY_PEPPER (openssl rand -hex 32), SMTP_*, STRIPE_WEBHOOK_SECRET
install -d -m 700 secrets
cp /secure/location/license-signing-key.pem /secure/location/update-signing-key.pem secrets/
chmod 644 secrets/*.pem                 # readable by the container user (1654); the directory stays 700
docker compose up -d --build
curl -s http://127.0.0.1:8080/health    # {"status":"ok"}
echo 'a long unique password' | docker compose exec -T admin-portal dotnet DownholeStudio.AdminPortal.dll create-admin --email ops@quakelogic.net --password-stdin
```

Both applications apply database migrations on start (EF Core serialises concurrent migrations with
a database lock). The SQL schema is also available as
`server/DownholeStudio.Server.Data/Migrations/schema.sql` for review or manual application.

Reverse-proxy routing:

| Public URL | Upstream | Notes |
|---|---|---|
| `https://licensing.quakelogic.net/` | `127.0.0.1:8080` | licence API; forward `X-Forwarded-For` and `X-Forwarded-Proto` |
| `https://licensing-admin.internal/` (VPN only) | `127.0.0.1:8081` | admin portal |

The desktop application's licensing URL is a build-time setting of the application; it must match
the public URL of the licence server.

## Operations

* Logs: `docker compose logs -f license-server` (structured, without licence keys or tokens).
* Upgrades: `git pull && docker compose up -d --build` (migrations run automatically).
* Backups: see [operations](../licensing/operations.md#backup-and-restore).
* Development e-mail capture: `docker compose --profile dev up -d mailpit`, then set
  `SMTP_HOST=mailpit`, `SMTP_PORT=1025` and open `http://127.0.0.1:8025`.
