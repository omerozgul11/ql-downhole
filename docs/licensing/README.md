# Licensing

Downhole Studio is licensed **per computer**: one seat = one activated computer. Every computer may
run a **14-day trial** once. The licensing server is authoritative; the desktop application verifies
signed tokens offline and never depends on permanent connectivity.

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
