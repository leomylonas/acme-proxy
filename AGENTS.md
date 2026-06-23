# AGENTS.md

Engineering guide for working on **AcmeProxy**. The [README](README.md) is the user-facing
overview (what it does, how to configure and run it); this file is for people and agents
changing the code. It captures the architecture, the non-obvious contracts, and the
gotchas that the original implementation plan and the e2e effort surfaced.

## What this service is

A single-project ASP.NET Core 10 service that presents an
[RFC 8555](https://datatracker.ietf.org/doc/html/rfc8555) ACME **server** to internal
sandboxed clients (certbot, cert-manager) and acts as an ACME **client** against Let's
Encrypt on their behalf. It fulfils `dns-01` challenges itself via the HestiaCP API, so
internal clients never touch DNS — they just poll their order until the certificate is ready.

State persists to SQLite via EF Core. Packaged as a Docker image.

## Build, test, run

```bash
dotnet test                                   # full unit/integration suite (58 tests)
dotnet run --project src/AcmeProxy            # Development: local db + LE staging
dotnet ef migrations add <Name> --project src/AcmeProxy
docker compose up --build                     # tests run during image build; image fails if tests fail
```

E2E tests live in `tests/AcmeProxy.E2ETests` and are skipped by ordinary `dotnet test`:

- **Pebble** (hermetic, runs in CI): set `ACMEPROXY_E2E=1`, needs Docker. Real certes path,
  no secrets. Set `ACMEPROXY_E2E_LOG=1` for debug logging.
- **Live LE staging** (manual): `tests/AcmeProxy.E2ETests/run-live-e2e.sh` reads HestiaCP
  creds from `src/AcmeProxy/appsettings.user.json` and exports `E2E_*` + `ACMEPROXY_E2E_STAGING=1`.

See [docs/E2E.md](docs/E2E.md).

## Architecture and request flow

```
client ──ACME──▶ AcmeController ──▶ OrderFulfilmentService ──┬─ LetsEncryptClient (certes) ──▶ Let's Encrypt
                                                             ├─ IDnsProviderPlugin (HestiaCP) ─▶ HestiaCP API
                                                             └─ IDnsPropagationPoller ─────────▶ public resolvers
                 AcmeProxyDbContext (SQLite)
```

`OrderFulfilmentService.FulfilAsync(challengeId)` is fire-and-forget, triggered by
`POST /acme/challenge/{id}`. Its ordered steps:

1. Load challenge (+ authorization + order); return early if not `pending` (idempotent).
2. `processing` → create LE order → persist `LeOrderUrl`, token, computed TXT value.
3. Add `_acme-challenge` TXT via HestiaCP; persist the returned record ID.
4. Poll public resolvers until **all** observe the TXT value.
5. Tell LE to validate; poll until `valid`.
6. Best-effort delete the TXT record; mark challenge/authz `valid`, order `ready`.
7. On any failure: best-effort DNS cleanup, mark everything `invalid` with the error.

A per-domain `SemaphoreSlim` (keyed `ConcurrentDictionary`) serialises fulfilment for the
same domain to avoid overlapping TXT records.

## Key invariants and non-obvious contracts

**These were learned the hard way — do not regress them.**

- **HestiaCP Access Key API**: POST `x-www-form-urlencoded` to `{BaseUrl}/api/` (NOT
  `/api/v1/exec/`). Auth is a single field `hash=<accesskey>:<secretkey>` (NOT separate
  `accesskey`/`secretkey` params). Commands: `cmd=v-add-dns-record`, args `arg1..argN`. Add
  `returncode=yes` when you expect a numeric status (`"0"` = success); omit it for commands
  returning JSON (e.g. `v-list-dns-records`). The original implementation plan's API contract
  was wrong on all three points.
- **`LetsEncryptClient` is a singleton.** It holds the certes `AcmeContext` (account + key)
  established once by `InitialiseAsync`. It must NOT be scoped — a scoped instance loses the
  context and throws "InitialiseAsync must be called before use". It reaches the scoped
  DbContext via `IServiceScopeFactory`.
- **LE issues asynchronously.** After `Finalize`, poll the order to `valid` before
  `Download`. Pebble issues instantly so this is easy to miss; real LE moves
  pending → processing → valid and the cert URL is null until then.
- **Do not call `chain.ToPem()`** in `FinalizeOrderAsync`. certes resolves issuers against
  its built-in CA store and throws for unknown roots (e.g. Pebble's test root). Concatenate
  `chain.Certificate.ToPem()` + each `chain.Issuers[*].ToPem()` manually.
- **Only public key material is stored.** `ClientAccount` holds the client's public JWK
  presented at `new-account`; the `Id` (Guid) is the `kid`. JWS is verified on every POST —
  both `jwk` (new-account) and `kid` (everything else, matched against the stored key).
  Unknown `kid` is rejected.
- **Nonces are single-use** (`NonceService`), validated on every POST; bad/used → `badNonce`.
- **Entity IDs are `Guid`** (route constraints `{id:guid}`), except `LeAccount.Id` (int).
- **DB must persist `/data`.** Losing the SQLite volume loses the LE account key and all
  client accounts (clients then get `accountDoesNotExist`).

## Domain allow-list semantics (`DomainWhitelist`)

With `AllowedDomains: ["example.com"]`: an identifier is accepted if it equals, is a
sub-domain of, or is a wildcard (`*.example.com`) of a listed domain. `evil.com` and
`notexample.com` are rejected (`rejectedIdentifier`, 403).

## Conventions

- One logical class/interface per file (e.g. `ILetsEncryptClient`, `LeOrderContext`,
  `LetsEncryptClient` are separate files). Keep it that way.
- All config lives under the `Proxy` section, bound to `ProxyOptions` and nested option
  classes in the `AcmeProxy.Configuration` namespace.
- `Program.cs` is a real `class Program` with `Main`; tests reference it via
  `WebApplicationFactory<Program>`.
- HestiaCP and Let's Encrypt HTTP traffic flows through named `IHttpClientFactory` clients
  carrying `LoggingHttpMessageHandler` — full request/response bodies log at `Debug`.

## Testing notes

- Unit/integration tests use `WebApplicationFactory<Program>` + EF Core InMemory with a
  shared `InMemoryDatabaseRoot`, stubbing `ILetsEncryptClient`, `IDnsProviderPlugin`,
  `IDnsPropagationPoller`.
- **Test isolation depends on the `Testing` environment.** Factories call
  `UseEnvironment("Testing")`, and `Program` skips loading `appsettings.user.json` under
  `Testing` — otherwise developer-local config (real domains/creds) bleeds into unit tests.
  Preserve both halves of this.

## Not implemented (current version)

- `revoke-cert` and `key-change` return `501`.
- Fulfilment is fire-and-forget; a process exit mid-flight can leave an order `processing`.
  Re-POSTing the challenge is safe (idempotent on non-`pending`). A durable work queue is the
  robustness upgrade.

## Security

`appsettings.user.json`, `.env`, and `appsettings.*.local.json` are git-ignored and may hold
live HestiaCP credentials — never commit them. The HestiaCP keys and LE account key live in
the SQLite volume; protect it.
