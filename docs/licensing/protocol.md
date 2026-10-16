# Licensing protocol

All calls are HTTPS `POST` with JSON (camelCase, ASP.NET Core "web" defaults) and return a
`LicenseResponse`:

```json
{ "success": true, "token": "dhs1.…", "errorCode": null, "message": "…", "serverTime": "2026-10-10T09:00:00+00:00", "devices": null }
```

Contracts are defined once in `src/DownholeStudio.Licensing/Contracts.cs` and shared by the client and
the server.

## Device fingerprint

```
deviceHash   = SHA-256("dhs-device-v1|" + normalise(MachineGuid) + "|" + normalise(SMBIOS system UUID) + "|" + productSalt)
hardwareHash = SHA-256("dhs-hw-v1|" + normalise(SMBIOS system UUID) + "|" + normalise(baseboard serial) + "|" + productSalt)
```

`normalise` keeps letters and digits, upper-cases, and rejects placeholder values (all zeros, all F,
"To Be Filled By O.E.M.", …). MachineGuid comes from `HKLM\SOFTWARE\Microsoft\Cryptography` (64-bit
view); SMBIOS from `GetSystemFirmwareTable('RSMB')`. RAM, disks, network adapters, MAC and IP
addresses are not used, so ordinary hardware changes do not invalidate a licence. Reinstalling
Windows changes `deviceHash` but not `hardwareHash`; the server uses the latter only to stop trial
resets.

## Calls

| Call | Request | Success | Typical errors |
|---|---|---|---|
| Start trial `/api/v1/trials/start` | product, fingerprint, appVersion, osVersion, machineName?, email? | trial token | `trial_expired`, `device_blocked`, `wrong_product` |
| Activate `/api/v1/licenses/activate` | licenseKey, email, product, fingerprint, appVersion, osVersion, machineName? | licence token | `invalid_key`, `email_mismatch`, `license_revoked`, `license_suspended`, `license_expired`, `activation_limit` (+ `devices`), `device_blocked` |
| Validate `/api/v1/licenses/validate` | token, fingerprint, appVersion | fresh token | `invalid_token`, `device_deactivated`, `license_revoked`, `license_suspended`, `license_expired`, `trial_expired` |
| Deactivate `/api/v1/licenses/deactivate` | token, fingerprint, reason? | — | `too_many_transfers`, `device_deactivated`, `invalid_token` |
| Latest release `GET /api/v1/updates/latest?product=&channel=&current=` | — | `SignedEnvelope` with `UpdateManifest` | 204 = up to date |

Rate limits answer `429` with `errorCode = rate_limited`. Unexpected server errors answer `500` with
`errorCode = server_error`; the client treats both like a network failure and keeps its current
state.

## Token format

```
dhs1.<keyId>.<base64url(claims JSON)>.<base64url(signature)>
```

The signature is ECDSA P-256 with SHA-256 in IEEE P1363 form (r‖s), over the ASCII text before the
last dot. The application embeds only public keys (`EmbeddedKeys`); the private key exists only on
the server.

| Claim | Meaning |
|---|---|
| `typ` | `trial` or `license` |
| `lid` | licence id (licence tokens) |
| `prd`, `ed` | product (`downhole-studio`) and edition |
| `dev` | device hash the token is bound to |
| `to`, `em`, `kh` | licensed to, e-mail, last four characters of the key |
| `iat` | server time of issue (also the "last verified" time) |
| `exp` | end of the entitlement (subscription end or trial end); absent = perpetual |
| `mnt` | maintenance (updates) included until |
| `ovu` | offline validity: the token must be refreshed online before this time |
| `ts`, `te` | trial start and end |
| `seats`, `feat`, `act` | seat count, feature list, activation id |

Paid tokens: `ovu = iat + 30 days`. Trial tokens: `ovu = min(iat + 3 days, te)`.

## Update manifests

`SignedEnvelope { keyId, payload, signature }` where `payload` is base64url JSON of
`UpdateManifest { product, channel, version, publishedAt, releaseNotes, installerUrl, sha256, size, isMajor, minimumVersion }`
signed with the **update** key (`dhs-upd-2026a`), never with the licence key. The application
verifies the signature, downloads the installer and verifies its SHA-256 before running it. The beta
channel also receives stable releases; the newest version wins (`1.2.0` > `1.2.0-beta.1`).
