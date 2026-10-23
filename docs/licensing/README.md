# Licensing

Downhole Studio is licensed **per computer**: one seat is one activated computer.

The shipped application uses **offline activation**: the customer sends the computer code and receives a signed activation key for that computer, and nothing connects to the internet. See [offline-activation.md](offline-activation.md).

The online licensing server documented below remains implemented, for when one is deployed. It adds `DHST-…` keys, self-service transfers and a trial; the shipped application does not offer the trial.

* [Offline activation: computer codes, activation keys, issuing keys](offline-activation.md)
* [Protocol and token format](protocol.md)
* [Client states, offline use and clock rules](client-states.md)
* [Operations: keys, rotation, first admin, Stripe, backup](operations.md)
* Deployment: [../deployment/license-server.md](../deployment/license-server.md)
* Server overview and configuration: [../../LICENSE-SERVER.md](../../LICENSE-SERVER.md)

Guarantees:

* Licence problems never modify, delete or lock project files. Without a valid licence the
  application opens projects read-only.
* Only salted hashes identify a computer. The machine name is sent only if the user ticks the box.
  No engineering data is ever sent to the licensing server.
* Licence keys are stored only as keyed hashes (HMAC-SHA256 with a server pepper); payment-card data
  is never received or stored.
