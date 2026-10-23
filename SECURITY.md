# Security

## Desktop application
* **No engineering data leaves the computer.** Licensing sends only what it needs (licence key and
  e-mail at activation, salted SHA-256 hashes of hardware identifiers, application and OS version,
  and the computer name only if the user allows it). No telemetry.
* **Signed licences.** Tokens are ECDSA P-256 signatures by the server; the application holds only
  public keys (`EmbeddedKeys.cs`). A local file saying "licensed" has no effect. The local licence
  state is encrypted (DPAPI current-user on Windows, AES-GCM elsewhere); a copied or tampered store
  is treated as absent.
* **Clock rollback.** The highest local time seen and the server-signed issue time are compared with
  the clock; a large backward step requires online validation (no accusation, no data loss).
* **Licence failures never touch data.** Without a valid licence the application opens projects
  read-only. No licence state deletes or modifies project files.
* **Updates** are accepted only with a valid signature from the separate update key, over HTTPS, and
  with matching SHA-256 and size.
* **Files.** Project saves are atomic with a backup; imports read files without modifying them;
  embedded originals are verified by SHA-256. CSV exports neutralise spreadsheet formula injection.
* **Logs** are structured JSON lines with licence keys, tokens and e-mail addresses redacted. The
  diagnostic package contains logs, version and system basics; survey files only if the user adds
  them.

Offline activation keys are ECDSA P-256 signed tokens bound to one computer code. The application verifies them against the embedded public key; if the operating system's crypto provider cannot verify P-256, the identical check runs in managed code (`ManagedP256`). Neither path ever needs a private key, and neither touches the network. Details: [docs/licensing/offline-activation.md](docs/licensing/offline-activation.md).

## Licence server and admin portal
TLS behind a reverse proxy (HSTS, HTTPS redirection), per-IP rate limiting (stricter on trial and
activation), licence keys stored only as HMAC-SHA256 with a server pepper, keys generated from a
cryptographic RNG (80 bits, Crockford base-32), signing keys from files/environment only, Stripe
webhooks verified (HMAC-SHA256, 5-minute tolerance, constant-time compare, idempotent), no card
data stored. The admin portal uses PBKDF2 password hashes, lockout after 5 failures, secure
HttpOnly SameSite=Strict cookies, antiforgery on every form, security headers, data-protection keys
in the database, and an audit record (who, what, when, before/after, IP) for every administrative
action. Details: [LICENSE-SERVER.md](LICENSE-SERVER.md).

## Secrets
No secrets are committed: `.gitignore` excludes `*.pem`, `*.pfx`, `.env` and `secrets/`. Production
signing keys live outside the repository (see [DEPLOYMENT.md](DEPLOYMENT.md)).

## Reporting a vulnerability
E-mail security@quakelogic.net with details; please do not open a public issue.
