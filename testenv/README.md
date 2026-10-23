# Local test environment

A complete Downhole Studio test setup that runs on one Linux machine with Docker. You use it from a web browser:
- the real application, running in its Linux build;
- the licence server and the admin portal;
- a test mailbox;
- a file browser for reports and test data.

It is for testing only. Nothing in it is used by customers.

```bash
testenv/start.sh                # first run builds everything (a few minutes) and prints addresses and passwords
testenv/start.sh --rebuild-app  # after changing the application code
testenv/stop.sh                 # stop (data is kept)
testenv/stop.sh --reset         # stop and delete all test data: licences, projects, settings
```

| Port | Service | What to do there |
|---|---|---|
| 50010 | **Downhole Studio** in the browser (noVNC) | Use the application normally. The VNC password is printed by `start.sh`. Closing the application restarts it. |
| 50011 | Licence server API | Used by the application. A Windows PC on the same network can use it too: in the installed app, set **Settings → Advanced → Licensing server** to `http://<server>:50011/` and restart. |
| 50012 | Admin portal | Create customers and licence keys, then activate one in the application; revoke, transfer, inspect devices and the audit log. |
| 50013 | Files | Download reports and exports (`Downhole Studio`). The five example SEG-2 surveys are in `Test data`, each with `TRUE-MODEL.txt`. Upload your own records to `Uploads`. |
| 50014 | Test mailbox | Licence and trial e-mails sent by the licence server are delivered here; nothing leaves the machine. |

Inside the application, the file dialogs show `Test data`, `Uploads` and `Downhole Studio` in the home folder.

## How it is built

- **Isolation.** Everything runs in the Docker Compose project `dhs-testenv` (`docker-compose.yml`), separate from the production deployment in `server/`.
- **Secrets.** Database password, key pepper, VNC, admin and file-browser passwords are generated into `testenv/.env` (git-ignored).
- **Signing keys.** The licence server signs with the production keys from the folder named by `SIGNING_KEYS_DIR` in `server/.env`. This is why the unmodified application accepts its trials and licences. The keys are mounted read-only and never copied.
- **Application.** `app/` builds an Ubuntu image with a virtual display (Xvfb), a window manager (Openbox), x11vnc and noVNC. The self-contained `linux-x64` build of the application comes from `artifacts/publish/linux-x64`. The application is pointed at the test licence server.
- **Graphics.** The application draws on a real render thread here, as on Windows. The headless UI tests do not, so this environment catches rendering problems that the tests cannot.
