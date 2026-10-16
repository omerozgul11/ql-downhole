# Client licence states

`LicenseManager` (in `src/DownholeStudio.Licensing`) evaluates the stored token without a network
and refreshes it online when possible.

| State | When | Editing | Viewing projects |
|---|---|---|---|
| NotActivated | no token, or a token for another computer | no | yes |
| TrialActive | trial with more than 3 days left | yes | yes |
| TrialExpiring | 3 days or fewer left | yes | yes |
| TrialExpired | trial end passed (also remembered from the server) | no | yes |
| Licensed | valid licence, verified online within 7 days | yes | yes |
| LicenseOfflineGrace | verified more than 7 days ago, offline period not over | yes | yes |
| LicenseExpired | `exp` passed | no | yes |
| LicenseRevoked | server reported revoked or suspended | no | yes |
| ActivationLimitReached | last activation attempt found all seats used | no | yes |
| NetworkValidationRequired | `ovu` passed, or clock rollback suspected | no | yes |

Rules:

* **Offline use.** Paid licences work for 30 days without contact; the UI shows "License last
  verified N days ago". Trials need a check every 3 days. A failed refresh never deletes the token.
* **Clock rollback.** The highest local time ever seen is stored (encrypted). If the clock moves back
  more than 6 hours, or is more than 6 hours behind the server-signed `iat`, the state becomes
  NetworkValidationRequired with "Your system clock changed significantly. Connect to the internet
  to verify your license." The user is not accused of anything. A successful online validation while
  the local clock agrees with the server re-anchors the guard.
* **Local storage.** `%LocalAppData%\QuakeLogic\Downhole Studio\license.dat`, protected with DPAPI
  (current user) on Windows and AES-GCM keyed by the device hash elsewhere. A copied or damaged file
  is treated as "not activated".
* **Revocation** removes the token but keeps a "revoked" marker so the right explanation is shown.
* **Deactivation** removes the local token only after the server confirms.
