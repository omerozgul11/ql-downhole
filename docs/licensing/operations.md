# Licensing operations

## First administrator

```bash
# Docker deployment
echo 'a long unique password' | docker compose exec -T admin-portal \
    dotnet DownholeStudio.AdminPortal.dll create-admin --email ops@quakelogic.net --password-stdin
```

Passwords need at least 12 characters and are stored as ASP.NET Core Identity PBKDF2 hashes. Five
failed sign-ins lock the account for 15 minutes. Every sign-in, failure and administrative change is
written to `audit_log` and visible under **Audit log**.

## Issuing a licence by hand

Admin portal → **Licenses → New license** (or from a customer page). The key is displayed exactly
once and, if selected, e-mailed to the customer. The server keeps only an HMAC of the key, so a lost
key cannot be shown again: use **Reissue key**, which invalidates the previous key and e-mails the new
one.

Other licence actions: revoke, suspend, reactivate, set expiry, set maintenance, change seats, change
the self-service transfer limit, reset all activations (override), deactivate one computer
(override). Trials can be extended or blocked; devices can be blocked.

## Software releases

Admin portal → **Software releases**: version (`1.2.3` or `1.2.3-beta.1`), channel, release notes,
HTTPS installer URL, installer SHA-256, size, "major" flag (the application shows release notes
before installing). Only published releases are offered; withdrawing a release hides it immediately.

## Stripe

1. In Stripe, create a webhook endpoint `https://<licensing-host>/api/v1/payments/stripe/webhook`
   for `checkout.session.completed` and copy its signing secret into `Stripe__WebhookSecret`.
2. In the Checkout Session set `metadata.product = downhole-studio`, `metadata.edition`,
   `metadata.seats` (and optionally `metadata.company`). The customer e-mail comes from
   `customer_details.email`.
3. On a paid session the server creates/updates the customer, creates the licence, records the
   payment (amount, currency, Stripe ids only) and queues the Purchase Confirmation and License Key
   e-mails. Replays of the same event id are ignored.

Another provider can be added by implementing `IPaymentProvider` in `Server.Data`.

## E-mail

Messages are written to `email_outbox` in the same transaction as the change that caused them and
delivered by a background worker over SMTP (MailKit), with exponential back-off for up to 10
attempts. A reminder worker queues the Trial Expiration Reminder (3 days before the end, once, when
the trial was started with an e-mail address) and the License Renewal reminder (30 days before
expiry, once). The dashboard shows queued and failed messages.

## Key rotation

1. Generate a new pair: `openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out license-signing-key-2027a.pem`.
2. Add the new public key under a new key id (e.g. `dhs-lic-2027a`) to `EmbeddedKeys.LicenseKeys`
   in the application and ship that version. Keep the old id.
3. When enough clients run the new version, switch the server: `Licensing__SigningKeyPath` → new
   key, `Licensing__SigningKeyId=dhs-lic-2027a`, and add the old public key as
   `Licensing__PreviousPublicKeys__dhs-lic-2026a` so existing tokens still validate.
4. After 30 days (the paid offline period) the old key can be retired from both.

The update key is rotated the same way with `EmbeddedKeys.UpdateKeys` and `Updates__*`.

**Never change `LICENSING_KEY_PEPPER`** on a live system: every issued key would stop matching.

## Backup and restore

* Back up the PostgreSQL database daily (`pg_dump -Fc dhs_licensing`) and keep copies off-site.
* Back up the two private keys and the pepper in the company secret store; without them no new
  tokens can be signed that existing installations accept, and no key can be checked.
* Restore: create the database, `pg_restore`, start the services with the same keys and pepper.
