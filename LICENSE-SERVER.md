# Downhole Studio licensing server

The licensing back end consists of two ASP.NET Core 10 applications sharing one PostgreSQL 17
database:

| Component | Project | Purpose |
|---|---|---|
| Licence server | `server/DownholeStudio.LicenseServer` | Public HTTPS API used by the desktop application: trials, activation, validation, deactivation, update feed, Stripe webhook |
| Admin portal | `server/DownholeStudio.AdminPortal` | Internal Razor Pages site for QuakeLogic staff: customers, licences, devices, trials, activations, releases, search, audit log |
| Data layer | `server/DownholeStudio.Server.Data` | EF Core model, migrations, licensing/admin/payment/e-mail services |

The protocol, token format and client behaviour are described in [docs/licensing](docs/licensing/README.md).
Deployment details: [docs/deployment/license-server.md](docs/deployment/license-server.md).

## What the server decides

* **Trials** — 14 days per computer, starting at the first successful trial request. A trial is
  found again by the device hash *or* the hardware hash, so reinstalling the application, deleting
  local data or reinstalling Windows on the same hardware does not reset it. Trial tokens must be
  re-validated online at least every 3 days (never beyond the trial end).
* **Activation** — licence key (stored only as an HMAC with a server pepper) + customer e-mail +
  product + status + expiry + free seat. Re-activating an already activated computer returns a new
  token without consuming a seat. When all seats are used the response lists the activated
  computers so the customer can free one.
* **Validation** — paid tokens allow 30 days offline. Each online check returns a fresh token, or
  `license_revoked`, `license_suspended`, `license_expired` or `device_deactivated`.
* **Deactivation** — self-service, limited to 3 moves per licence per rolling 30 days
  (`too_many_transfers`); administrators can always reset or deactivate (audited override).
* **Payments** — Stripe Checkout `checkout.session.completed` (paid) creates the customer and
  licence, records the payment references (never card data) and queues the Purchase Confirmation and
  License Key e-mails, idempotently per Stripe event.
* **Updates** — the newest published release for the product/channel newer than the client's
  version, as a manifest signed with the separate update key.

Every licensing decision is written to `license_events`; every administrator action to `audit_log`.

## Quick start (local)

```bash
source .tools/env.sh
docker run -d --name dhs-pg-dev -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=dhs_licensing -p 127.0.0.1:55432:5432 postgres:17-alpine
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out /tmp/dhs-lic.pem
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out /tmp/dhs-upd.pem
export ConnectionStrings__Licensing="Host=127.0.0.1;Port=55432;Database=dhs_licensing;Username=postgres;Password=dev"
export Licensing__SigningKeyPath=/tmp/dhs-lic.pem Updates__SigningKeyPath=/tmp/dhs-upd.pem LICENSING_KEY_PEPPER=dev-pepper
dotnet run --project server/DownholeStudio.LicenseServer        # Development: migrates the database
echo 'a-long-dev-password' | dotnet run --project server/DownholeStudio.AdminPortal -- create-admin --email you@quakelogic.net --password-stdin
dotnet run --project server/DownholeStudio.AdminPortal          # Development allows plain HTTP
```

Development-generated keys only work with a desktop build whose `EmbeddedKeys` contain the matching
public key. Production keys are described below.

## Configuration

All secrets come from environment variables (or another configuration provider such as a secret
store); none are in the repository or the images.

| Setting (env var) | Used by | Meaning |
|---|---|---|
| `ConnectionStrings__Licensing` | both | PostgreSQL connection string |
| `LICENSING_KEY_PEPPER` / `Licensing__KeyPepper` | both | Secret pepper for licence-key HMACs. **Changing it invalidates every issued key.** |
| `Licensing__SigningKeyPath` or `LICENSING_SIGNING_KEY_PEM` | server | ECDSA P-256 PKCS#8 private key that signs tokens |
| `Licensing__SigningKeyId` | server | Key id in tokens (default `dhs-lic-2026a`) |
| `Licensing__PreviousPublicKeys__<kid>` | server | Old public keys still accepted during rotation |
| `Updates__SigningKeyPath` or `UPDATES_SIGNING_KEY_PEM` | server | ECDSA P-256 key that signs update manifests |
| `Updates__SigningKeyId` | server | Default `dhs-upd-2026a` |
| `Licensing__TrialDays`, `__TrialOfflineDays`, `__PaidOfflineDays`, `__DefaultMaxTransfersPer30Days` | server | 14, 3, 30, 3 |
| `Stripe__WebhookSecret` | server | `whsec_…` signing secret of the Stripe webhook endpoint |
| `Stripe__TermMonths` | server | Licence term for purchases (empty = perpetual) |
| `Stripe__MaintenanceMonths` | server | Maintenance included with a purchase (default 12) |
| `Smtp__Host`, `__Port`, `__User`, `__Password`, `__From`, `__StartTls` | server | Outgoing mail |
| `Email__SupportEmail`, `__DownloadUrl`, `__PrivacyUrl` | server | Text used in e-mails |
| `Database__MigrateOnStartup` | both | Apply EF migrations at start (safe to enable on both) |
| `RateLimiting__PermitsPerMinute`, `__StrictPermitsPerMinute` | server | Per-IP limits (120 general, 20 for trial/activate/deactivate) |
| `ForwardedHeaders__TrustAllProxies` | both | Trust `X-Forwarded-*` from the reverse proxy on a private network |
| `Https__Redirect` | server | Disable when TLS terminates at the proxy |
| `AdminPortal__AllowInsecureHttp` | portal | Development only; production requires HTTPS |
| `Workers__Enabled` | server | E-mail dispatch and reminder workers (default true) |

## Keys

Two ECDSA P-256 key pairs exist. The production private keys are kept outside the repository (on the
build host, mode 600, and in QuakeLogic's secret store); the server reads them from the folder named by
`SIGNING_KEYS_DIR` in `server/.env`:

* `license-signing-key.pem` (key id `dhs-lic-2026a`) — signs licence and trial tokens.
* `update-signing-key.pem` (key id `dhs-upd-2026a`) — signs update manifests.

The matching **public** keys are compiled into the desktop application
(`src/DownholeStudio.Licensing/EmbeddedKeys.cs`). A server signing with any other key produces
tokens the application refuses. See [key rotation](docs/licensing/operations.md#key-rotation).

## Endpoints

| Method | Path | Body / query | Response |
|---|---|---|---|
| POST | `/api/v1/trials/start` | `TrialStartRequest` | `LicenseResponse` |
| POST | `/api/v1/licenses/activate` (alias `/api/licenses/activate`) | `ActivationRequest` | `LicenseResponse` |
| POST | `/api/v1/licenses/validate` | `ValidationRequest` | `LicenseResponse` |
| POST | `/api/v1/licenses/deactivate` | `DeactivationRequest` | `LicenseResponse` |
| GET | `/api/v1/updates/latest` | `product`, `channel`, `current` | `SignedEnvelope` (200) or 204 |
| POST | `/api/v1/payments/stripe/webhook` | raw Stripe event + `Stripe-Signature` | 200 / 400 |
| GET | `/health` | — | 200 when the database is reachable |

## Tests

```bash
dotnet test tests/DownholeStudio.Licensing.Tests     # client: codec, keys, fingerprint, store, state machine
dotnet test tests/DownholeStudio.IntegrationTests    # real PostgreSQL in Docker + both applications in-process
```

The integration tests start a disposable `postgres:17-alpine` container named `dhs-pgtest-…`,
remove it afterwards and skip (with a message) when Docker is unavailable.
