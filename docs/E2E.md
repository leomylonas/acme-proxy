# End-to-end testing

AcmeProxy has two end-to-end tests in `tests/AcmeProxy.E2ETests`, both driving the full ACME
flow (new-account → new-order → challenge → finalize → download) through the real HTTP API.
They differ only in what sits behind the proxy.

## 1. Pebble e2e (hermetic, CI)

`PebbleEndToEndTests` runs AcmeProxy against a local [Pebble](https://github.com/letsencrypt/pebble)
ACME server, with the dns-01 challenge fulfilled via `pebble-challtestsrv`. This exercises the
**real certes order/validate/finalize path** with no external dependencies or secrets.

- The outbound ACME client points at Pebble (`Proxy:LetsEncrypt:DirectoryUrl`).
- `IDnsProviderPlugin` is swapped for one that writes TXT records into challtestsrv.
- `IDnsTxtResolver` queries challtestsrv's DNS server, so propagation polling mirrors what
  Pebble's validation authority sees.

Run it locally (Docker required):

```bash
ACMEPROXY_E2E=1 dotnet test tests/AcmeProxy.E2ETests/AcmeProxy.E2ETests.csproj
```

Without `ACMEPROXY_E2E=1` the Docker stack is never started and the test is skipped, so
ordinary `dotnet test` runs stay hermetic. The `pebble-e2e` job in
[.github/workflows/ci.yml](../.github/workflows/ci.yml) runs it on every push/PR.

## 2. Live Let's Encrypt staging e2e (manual)

`LiveStagingEndToEndTests` runs AcmeProxy against the **real** Let's Encrypt staging directory
using the **real** HestiaCP DNS provider and public-resolver propagation. This is the genuine
smoke test — the only one that validates the HestiaCP integration and real DNS propagation.

### Requirements

- A public domain whose DNS zone is managed by a reachable HestiaCP instance.
- HestiaCP Access Key credentials for the user that owns that zone.

> The `E2E_DOMAIN` should be the **apex of the HestiaCP-managed zone** (e.g. `test.example.com`
> where that is itself a zone in HestiaCP), or you should issue a wildcard. The proxy places the
> challenge at `_acme-challenge.<E2E_DOMAIN>` and calls HestiaCP with that domain as the zone, so
> issuing for an arbitrary deeper sub-domain of a larger zone will not match the record placement.

### Run locally

```bash
export ACMEPROXY_E2E_STAGING=1
export E2E_DOMAIN=test.example.com
export E2E_HESTIA_BASEURL=https://panel.example.com:8083
export E2E_HESTIA_ACCESSKEY=...
export E2E_HESTIA_SECRETKEY=...
export E2E_HESTIA_USERNAME=...
export E2E_ACCOUNT_EMAIL=admin@example.com   # optional

dotnet test tests/AcmeProxy.E2ETests/AcmeProxy.E2ETests.csproj \
  --filter "FullyQualifiedName~LiveStagingEndToEndTests"
```

The test is skipped unless `ACMEPROXY_E2E_STAGING=1` and all required variables are present.
Real validation + DNS propagation can take a few minutes (the test allows up to 6).

### Run in CI

The manual [e2e-staging workflow](../.github/workflows/e2e-staging.yml) (Actions →
"E2E (Let's Encrypt staging)" → Run workflow) runs the same test from the repository secrets
listed at the top of that file.

## Verifying with a real ACME client (optional)

The e2e tests act as the ACME client themselves. To additionally validate interop with a real
client, point certbot at a running instance (see the certbot example in the
[README](../README.md#client-examples)) issuing against staging.
