# AcmeProxy

AcmeProxy is a single-project ASP.NET Core service that presents an
[RFC 8555](https://datatracker.ietf.org/doc/html/rfc8555)-compatible **ACME server** to
internal, sandboxed clients (certbot, cert-manager) while acting as an **ACME client**
against Let's Encrypt on their behalf.

The key idea: internal clients never touch DNS. When a client asks AcmeProxy to validate a
`dns-01` challenge, AcmeProxy transparently fulfils that challenge itself — it places the
`_acme-challenge` TXT record via the HestiaCP API, waits for propagation, drives Let's
Encrypt through validation, and cleans up. The client simply polls its order until the
certificate is ready.

```
                          ┌──────────────────────────────────────────────────┐
                          │                  AcmeProxy                       │
  certbot /               │                                                  │
  cert-manager  ──ACME──▶ │  ACME server  ─▶ OrderFulfilmentService          │ ──ACME──▶  Let's Encrypt
  (sandbox)               │                    ├─ LetsEncryptClient (certes) │
                          │                    ├─ HestiaCP DNS provider      │ ──API──▶   HestiaCP
                          │                    └─ DNS propagation poller     │ ──DNS──▶   8.8.8.8 / 1.1.1.1
                          │                                                  │
                          │  SQLite (state: accounts, orders, certs)         │
                          └──────────────────────────────────────────────────┘
```

## How a certificate is issued

1. Client `POST /acme/new-account` — AcmeProxy stores the client's **public** key (JWK).
2. Client `POST /acme/new-order` for a domain on the allow-list — AcmeProxy persists a
   pending order, authorization, and a `dns-01` challenge.
3. Client `POST /acme/challenge/{id}` — AcmeProxy returns `processing` immediately and, in
   the background:
   1. Creates a matching order on Let's Encrypt (via `certes`).
   2. Computes the `_acme-challenge` TXT value and adds it through the HestiaCP API.
   3. Polls public resolvers until the TXT value has propagated everywhere.
   4. Tells Let's Encrypt to validate, and waits for `valid`.
   5. Deletes the TXT record (best-effort) and marks the order `ready`.
4. Client polls `GET /acme/order/{id}` until `ready`, then `POST .../finalize` with its CSR.
5. AcmeProxy finalizes against Let's Encrypt, stores the chain, and the client downloads it
   from `GET /acme/cert/{id}`.

## Technology stack

| Concern | Library |
|---|---|
| Web framework | ASP.NET Core 10.0 |
| ACME client (outbound) | `Certes` |
| DNS resolution (propagation polling) | `DnsClient.NET` |
| ORM / database | EF Core 10 + SQLite |
| JSON | `System.Text.Json` |
| HTTP (HestiaCP / Let's Encrypt) | `HttpClient` via `IHttpClientFactory` |
| Tests | xUnit, Moq, FluentAssertions, EF Core InMemory, `Mvc.Testing` |

## Configuration

All settings live under the `Proxy` section (plus the `Default` connection string). Every
value can be supplied via `appsettings.json` **or** environment variables using the standard
ASP.NET Core double-underscore convention (`Proxy__HestiaCP__AccessKey=...`).

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=/data/acmeproxy.db"
  },
  "Proxy": {
    "AllowedDomains": [ "example.com" ],
    "LetsEncrypt": {
      "UseStaging": true,
      "AccountEmail": "admin@example.com"
    },
    "HestiaCP": {
      "BaseUrl": "https://panel.example.com:8083",
      "AccessKey": "",
      "SecretKey": "",
      "Username": ""
    },
    "Dns": {
      "PropagationPollIntervalSeconds": 10,
      "PropagationTimeoutSeconds": 300,
      "ResolverAddresses": [ "8.8.8.8", "1.1.1.1" ]
    }
  }
}
```

| Setting | Description | Default |
|---|---|---|
| `ConnectionStrings:Default` | SQLite connection string. **Must point at a persistent volume** — it holds the Let's Encrypt account key and all client accounts. | `Data Source=/data/acmeproxy.db` |
| `Proxy:AllowedDomains` | Root-domain allow-list. An identifier is accepted if it equals, or is a sub-domain/wildcard of, a listed domain. | `[]` |
| `Proxy:LetsEncrypt:UseStaging` | Use the Let's Encrypt **staging** directory. Set `false` for production issuance. | `true` |
| `Proxy:LetsEncrypt:AccountEmail` | Contact email for the LE account created on first start. | `""` |
| `Proxy:HestiaCP:BaseUrl` | HestiaCP panel base URL (including port). | `""` |
| `Proxy:HestiaCP:AccessKey` / `SecretKey` | HestiaCP Access Key credentials. | `""` |
| `Proxy:HestiaCP:Username` | HestiaCP user that owns the DNS zone. | `""` |
| `Proxy:Dns:PropagationPollIntervalSeconds` | Seconds between propagation polls. | `10` |
| `Proxy:Dns:PropagationTimeoutSeconds` | Give up (order → `invalid`) after this long. | `300` |
| `Proxy:Dns:ResolverAddresses` | Public resolvers that must **all** observe the TXT value before validation proceeds. | `["8.8.8.8","1.1.1.1"]` |
| `Proxy:InitialiseLetsEncryptOnStartup` | Register/load the LE account at boot. Set `false` for tests/offline runs. | `true` |

### Domain allow-list semantics

With `AllowedDomains: ["example.com"]`:

| Identifier | Allowed |
|---|---|
| `example.com` | ✅ |
| `sub.example.com` | ✅ |
| `deep.sub.example.com` | ✅ |
| `*.example.com` | ✅ |
| `evil.com` | ❌ |
| `notexample.com` | ❌ |

## Running locally

```bash
# restore, build, test
dotnet test

# run (Development uses a local acmeproxy-dev.db and LE staging)
dotnet run --project src/AcmeProxy
```

The database is migrated automatically on startup. To create/inspect migrations manually:

```bash
dotnet ef migrations add <Name> --project src/AcmeProxy
dotnet ef database update --project src/AcmeProxy
```

## Running with Docker

The Dockerfile runs the full test suite during build — the image is not produced if tests
fail.

```bash
docker compose up --build
```

`docker-compose.yml` mounts a named volume at `/data` so the SQLite database (and therefore
the LE account key + client accounts) survives restarts. Configure it via the `Proxy__*`
environment variables shown in that file.

> ⚠️ **Persist `/data`.** If the volume is lost, the Let's Encrypt account key and all
> registered client accounts are gone. Clients holding a cached account will get
> `accountDoesNotExist` on their next request and must re-run `new-account`.

## ACME endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/acme/directory` | Directory of endpoints |
| `HEAD`/`GET` | `/acme/new-nonce` | Issue a replay nonce |
| `POST` | `/acme/new-account` | Register a client account (stores its public JWK) |
| `POST` | `/acme/new-order` | Create an order for allow-listed identifiers |
| `GET` | `/acme/orders/{id}` | Account order list (advertised by `new-account`; always empty) |
| `GET` | `/acme/order/{id}` | Order status |
| `GET` | `/acme/authz/{id}` | Authorization + `dns-01` challenge |
| `POST` | `/acme/challenge/{id}` | Begin background fulfilment |
| `POST` | `/acme/order/{id}/finalize` | Submit CSR, receive certificate URL |
| `GET` | `/acme/cert/{id}` | Download the PEM chain |
| `POST` | `/acme/revoke-cert`, `/acme/key-change` | `501 Not Implemented` |

All POST bodies are JWS (`application/jose+json`). Signatures are verified for both
`jwk`-based (new-account) and `kid`-based requests; `kid` requests are matched against the
public key stored at registration. Replay nonces are single-use.

## Client examples

### certbot

```bash
certbot certonly \
  --manual \
  --preferred-challenges dns \
  --manual-auth-hook /bin/true \
  --manual-cleanup-hook /bin/true \
  --server http://acmeproxy.internal/acme/directory \
  --non-interactive \
  -d sub.example.com
```

The `--manual-auth-hook /bin/true` and `--manual-cleanup-hook /bin/true` are no-ops —
AcmeProxy fulfils the `dns-01` challenge internally, so certbot never needs to touch DNS
itself. It simply polls until the order is ready.

### cert-manager

```yaml
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: acmeproxy
spec:
  acme:
    server: http://acmeproxy.internal/acme/directory
    privateKeySecretRef:
      name: acmeproxy-account-key
    solvers:
      - dns01:
          # AcmeProxy intercepts dns-01 fulfilment; any webhook/solver the cluster
          # accepts works because the proxy ignores the client's own DNS attempts.
          webhook: {}
```

## Logging & debugging

This service is expected to need diagnosis against real DNS/LE, so it logs verbosely at
`Debug`:

- **HestiaCP and Let's Encrypt HTTP traffic** (method, URL, request/response bodies, timing)
  via a logging `DelegatingHandler` on the named HTTP clients.
- Per-step fulfilment progress (LE order creation, TXT add, propagation wait, validation
  polling, cleanup).
- JWS parse/verify outcomes (nonce, kid, url) on every ACME POST.

Enable it by setting the log level (Development already does):

```jsonc
// appsettings.json / appsettings.Development.json
"Logging": { "LogLevel": { "Default": "Debug" } }
```

or `Logging__LogLevel__Default=Debug` as an environment variable. At `Information` level the
HTTP handler still logs a one-line summary per request.

## Project structure

```
src/AcmeProxy/
  Configuration/   ProxyOptions, LetsEncryptOptions, HestiaCPOptions, DnsOptions
  Controllers/     AcmeController (RFC 8555 endpoints)
  Data/            AcmeProxyDbContext, Entities/ (LeAccount, ClientAccount,
                   ProxyOrder/Authorization/Challenge/Certificate)
  Migrations/      EF Core migrations (applied automatically on startup)
  HestiaCP/        HestiaCPClient (IDnsProviderPlugin over the Access Key API)
  LetsEncrypt/     LetsEncryptClient + ILetsEncryptClient + LeOrderContext (certes wrapper)
  Models/Acme/     ACME request/response DTOs (one per file)
  Services/        NonceService, AcmeJws, DomainWhitelist, DnsPropagationPoller,
                   OrderFulfilmentService, LoggingHttpMessageHandler, DNS resolver
  Program.cs       Host wiring (class with Main)
tests/AcmeProxy.Tests/
  Controllers/ Services/ HestiaCP/ LetsEncrypt/ Data/ Helpers/
```

## Testing

```bash
dotnet test
```

58 unit/integration tests cover the nonce service, DNS propagation poller, HestiaCP client,
order-fulfilment orchestration, JWS/account verification, the full controller surface (via
`WebApplicationFactory`), and the EF model.

Two end-to-end tests live in `tests/AcmeProxy.E2ETests` and drive the complete ACME flow:
a hermetic **Pebble** run (real certes path, no secrets — runs in CI) and a manual **live
Let's Encrypt staging** run (real HestiaCP + DNS). See [docs/E2E.md](docs/E2E.md). Ordinary
`dotnet test` keeps these skipped unless explicitly enabled.

## Security notes

- **Only public key material is stored.** Client private keys never leave the client; the
  proxy persists the public JWK presented at `new-account` and verifies later `kid` requests
  against it.
- **JWS signatures are always verified** — no request path skips verification.
- **Nonces are single-use** and validated on every POST.
- The HestiaCP secret/access keys and the LE account key live in the SQLite volume; protect
  that volume accordingly.

## Not implemented (this version)

- `revoke-cert` and `key-change` return `501`.
- Background fulfilment is fire-and-forget; if the process exits mid-flight an order may be
  left in `processing`. Re-POSTing the challenge is safe (fulfilment is idempotent on
  non-`pending` status), and a durable work queue would be the robustness upgrade.
