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
                                        # (or set SIGNING_KEYS_DIR in .env to the folder holding the keys; the
                                        #  files must then also be readable by the container user 1654)
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

## Issuing activation keys from the office network

The shipped application activates offline, so issuing keys needs only PostgreSQL and the admin portal. The portal refuses plain HTTP, so a small Caddy proxy provides HTTPS on the office network:

```bash
cd server
# In .env: DHS_CONTAINER_USER (the owner of the signing key files), ADMIN_LAN_ADDRESS, ADMIN_LAN_BIND, ADMIN_LAN_PORT, ADMIN_EMAIL, ADMIN_PASSWORD
docker compose --profile lan up -d postgres admin-portal admin-proxy
printf '%s\n' "$ADMIN_PASSWORD" | docker compose exec -T admin-portal dotnet DownholeStudio.AdminPortal.dll create-admin --email "$ADMIN_EMAIL" --password-stdin
```

Open `https://<address>:<port>/`. The certificate comes from Caddy's own authority, so browsers warn until it is trusted. Never forward this port from the internet.

Back up after issuing keys: `server/backup.sh` writes a compressed copy of the database outside the repository and keeps the newest 30. Also keep safe copies of `server/.env` and the signing keys: the key pepper in `.env` is needed to use a restored database.
