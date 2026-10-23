# Deployment and releases

## Release checklist
1. Update `VersionPrefix` in `Directory.Build.props`; add release notes.
2. On a Windows build machine with the code-signing certificate:
   `scripts\build.ps1 -Version x.y.z -Sign -CertificateThumbprint <thumbprint>` (or `DHS_SIGN_COMMAND`
   for a cloud HSM signing tool). This runs all tests, regenerates the validation report, signs the
   application binaries, builds the installer (signed installer and uninstaller) and verifies the
   signature. Without a certificate use `scripts/build.sh` on Linux; the build is then unsigned and
   the release notes must say so.
3. Test the installer on a clean Windows 10 22H2 and Windows 11 machine: install, start, trial,
   open the sample, generate a report, upgrade from the previous version, uninstall (projects and
   settings must remain).
4. Publish the installer. The README's single download link points to
   `raw/main/download/DownholeStudio-Setup.exe`, so customers download it directly from the
   repository. Replace that file (always under the same name) and its checksum, then commit and push:
   ```bash
   cp artifacts/installer/DownholeStudio-Setup-x.y.z.exe download/DownholeStudio-Setup.exe
   (cd download && sha256sum DownholeStudio-Setup.exe > DownholeStudio-Setup.exe.sha256)
   git add download && git commit -m "Downhole Studio x.y.z installer" && git push
   ```
   Do the same for the client package: build it with `scripts/package-client.py` (see BUILD.md) and copy it to
   `download/DownholeStudio-Client-Package.zip`, with its `.sha256`. Copy the training video to
   `docs/marketing/Downhole-Studio-Training-Video.mp4`. Every file must stay under GitHub's 100 MB file limit. Optionally, also create a versioned
   GitHub release with `scripts/publish-release.sh` (reads `GITHUB_TOKEN` from `.env`).
5. In the admin portal → Software Releases, create the release (version, channel, notes, installer
   URL, SHA-256, size, major flag) and publish it. The licence server then offers it through the
   signed update feed.

## Code signing
Authenticode signing needs an OV/EV code-signing certificate held by QuakeLogic (hardware token or
cloud HSM). Neither the certificate nor any private key is stored in this repository. Inno Setup is
given the sign tool as `dhssign` (`/Sdhssign=…`) and signs `Setup.exe` and the uninstaller;
`build.ps1` signs `DownholeStudio.exe` and the application DLLs first and verifies with
`signtool verify /pa`.

## Update feed
`GET /api/v1/updates/latest?product=downhole-studio&channel=stable&current=1.0.0` returns a manifest
(version, notes, installer URL, SHA-256, size, major flag) signed with the update key (ECDSA P-256,
key id `dhs-upd-2026a`). The application verifies the signature with the embedded public key,
requires HTTPS, downloads, verifies size and SHA-256 and only then starts the installer
(`/SILENT /CLOSEAPPLICATIONS`), which relaunches the application. Settings → Updates: automatic
(minor updates), notify only, or manual; stable or beta channel. Major upgrades always show the
release notes and ask first.

## Licence server
See [LICENSE-SERVER.md](LICENSE-SERVER.md) and [docs/deployment/license-server.md](docs/deployment/license-server.md):
Docker Compose with PostgreSQL 17, the licence server and the admin portal behind a TLS reverse
proxy. Secrets (database password, key pepper, signing keys, Stripe and SMTP credentials) come from
environment variables or mounted secret files only.

The application's server address is set in `src/DownholeStudio.App/appsettings.json`
(`LicenseServerUrl`, default `https://licensing.quakelogic.net/`) and must match the deployed
server. Users can override it under Settings → Advanced.

## Signing keys
The production licence and update signing keys were generated with `openssl` (ECDSA P-256) and are
stored outside the repository, on the build host and in QuakeLogic's secret store (never in git). Their
public keys are embedded in `src/DownholeStudio.Licensing/EmbeddedKeys.cs`. Keep encrypted backups
in QuakeLogic's secret store. To rotate: generate a new key (`dhs keygen`), add its public key under
a new id, release the application, then switch the server to the new key.
