# Offline activation

The shipped application activates **offline**. It has no licensing server configured (`LicenseServerUrl` is empty in `src/DownholeStudio.App/appsettings.json`) and makes no network connections. Every installation is still licensed per computer.

## How it works

1. **The computer code.** The application derives a *computer code* from the computer's device hash (Windows MachineGuid plus SMBIOS UUID, salted, SHA-256; see `Fingerprint.cs`).
   - It is 24 Crockford base-32 characters (the first 120 bits of the hash) plus one check character, shown as five groups of five: `7K3M9-QPZX2-AB4HV-N0R5T-W8YCD`.
   - The check character catches typing errors.
   - The code cannot be turned back into hardware identifiers.
   - It stays the same across reinstalls and updates. It changes when Windows is reinstalled or the computer is replaced.
2. **The request.** The customer sends the code to QuakeLogic. The activation window has **Copy Code** and **E-mail to QuakeLogic**, which opens a pre-filled e-mail to `ActivationEmail` (default `sales@quakelogic.net`).
3. **The key.** QuakeLogic issues an *activation key* for that code. It is a licence token (`dhs1.<kid>.<payload>.<signature>`, see [protocol.md](protocol.md)) with:
   - type `license-offline`;
   - `dev` = the computer code;
   - the licensee, an optional end date and maintenance date;
   - `ovu` = 9999-12-31 (no online check ever).

   It is signed with the production licence key (ECDSA P-256), whose private part never leaves QuakeLogic.
4. **Activation.** The customer pastes the key, or opens the `.dhslic` activation file. The application checks four things, all offline:
   - the signature, against the public key embedded in the application;
   - the product;
   - the token type;
   - that the code in the key matches this computer.

   It then stores the token encrypted (DPAPI on Windows).

### Rules enforced by the application

| Rule | Behaviour |
|---|---|
| Key for another computer | Refused. The message shows both codes. |
| Tampered key, or key signed by an unknown key | Refused as "not valid". |
| End date | Checked against the latest time this computer has ever seen. Setting the clock back cannot extend a licence. |
| Remove activation | Deletes the token locally (Settings → License). The seat is freed in the admin portal when the customer reports the move. |

**Signature verification fallback.** Verification uses the operating system's ECDSA implementation. If that provider cannot do P-256, the identical check runs in managed code (`ManagedP256.cs`). Examples: restricted crypto policies, or compatibility layers such as Wine, whose Windows crypto library lacks P-256. The managed verifier is cross-checked against the platform implementation in `ManagedP256Tests`.

## Issuing keys

**Admin portal.** Open the licence, go to **Offline activation**, and enter the computer code and an optional computer name. Select **Issue activation key**. The portal:
- validates the code, including the check character;
- requires an active, unexpired licence and a free seat;
- records the computer and the activation (it counts against the seats);
- writes the audit log and a licence event;
- shows the key once, with **Copy key** and **Download activation file**;
- optionally e-mails it to the customer (`activation-key` template).

To move a licence, deactivate the old computer's activation on the licence page, then issue a key for the new code. Offline keys cannot be revoked remotely. A key stays valid on its computer until its end date. Use end dates (for example annual) when that matters.

**Command line**, on a machine that holds the private key:

```bash
dotnet run --project tools/DownholeStudio.Cli -- license issue \
  --computer 7K3M9-QPZX2-AB4HV-N0R5T-W8YCD --to "Riverside Geotechnical" --email engineer@example.com \
  --key <folder>/license-signing-key.pem [--expires 2027-12-31] [--maintenance 2027-10-31] [--out activation.dhslic]
```

Before printing, the CLI checks that the application's embedded public key accepts the key it signed. CLI-issued keys are not recorded in the admin portal's database.

## Online activation

Online activation (`DHST-…` keys, the 30-day offline grace period, server-side deactivation) is still implemented and tested. It becomes available when `LicenseServerUrl` is set to a deployed licensing server. Both kinds of activation are then accepted.
