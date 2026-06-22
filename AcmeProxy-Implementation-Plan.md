# AcmeProxy — Full Implementation Plan

## Overview

A single-project ASP.NET Core service that:

- Presents an RFC 8555-compatible ACME server to internal sandboxed clients (certbot, cert-manager)
- Acts as an ACME client against Let's Encrypt using `certes`
- Fulfils DNS-01 challenges internally via the HestiaCP API
- Persists state to SQLite via Entity Framework Core
- Is packaged as a Docker/OCI container image

The service intercepts DNS-01 challenge fulfilment — rather than the internal client setting the TXT record, the service does it transparently. The internal client simply polls order status until the certificate is ready.

---

## Technology Stack

| Concern | Library |
|---|---|
| Web framework | ASP.NET Core 10.0 |
| ACME client (outbound) | `certes` |
| DNS resolution (propagation polling) | `DnsClient.NET` |
| ORM | Entity Framework Core 10 |
| Database | SQLite (`Microsoft.EntityFrameworkCore.Sqlite`) |
| JSON | `System.Text.Json` |
| HTTP client (HestiaCP) | `HttpClient` via `IHttpClientFactory` |
| Unit testing | xUnit, Moq, FluentAssertions, `Microsoft.EntityFrameworkCore.InMemory` |

---

## Project Structure

```
AcmeProxy/
├── src/
│   └── AcmeProxy/
│       ├── Controllers/
│       │   └── AcmeController.cs
│       ├── Data/
│       │   ├── AcmeProxyDbContext.cs
│       │   └── Entities/
│       │       ├── LeAccount.cs
│       │       ├── ProxyOrder.cs
│       │       ├── ProxyAuthorization.cs
│       │       ├── ProxyChallenge.cs
│       │       └── ProxyCertificate.cs
│       ├── HestiaCP/
│       │   ├── HestiaCPClient.cs
│       │   └── HestiaCPOptions.cs
│       ├── LetsEncrypt/
│       │   ├── LetsEncryptClient.cs
│       │   └── LetsEncryptOptions.cs
│       ├── Services/
│       │   ├── IDnsProviderPlugin.cs
│       │   ├── HestiaCPDnsProvider.cs
│       │   ├── DnsPropagationPoller.cs
│       │   ├── OrderFulfilmentService.cs
│       │   └── NonceService.cs
│       ├── Configuration/
│       │   └── ProxyOptions.cs
│       ├── Models/
│       │   └── Acme/
│       │       ├── DirectoryModel.cs
│       │       ├── NewAccountRequest.cs
│       │       ├── NewOrderRequest.cs
│       │       ├── OrderResponse.cs
│       │       ├── AuthorizationResponse.cs
│       │       ├── ChallengeResponse.cs
│       │       └── FinalizeRequest.cs
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       └── Program.cs
├── tests/
│   └── AcmeProxy.Tests/
│       ├── Controllers/
│       │   └── AcmeControllerTests.cs
│       ├── Services/
│       │   ├── DnsPropagationPollerTests.cs
│       │   ├── OrderFulfilmentServiceTests.cs
│       │   └── NonceServiceTests.cs
│       ├── HestiaCP/
│       │   └── HestiaCPClientTests.cs
│       ├── LetsEncrypt/
│       │   └── LetsEncryptClientTests.cs
│       ├── Data/
│       │   └── AcmeProxyDbContextTests.cs
│       └── Helpers/
│           ├── TestDbContextFactory.cs
│           └── AcmeJwsHelper.cs
├── Dockerfile
├── docker-compose.yml
└── AcmeProxy.sln
```

---

## Solution File

```
AcmeProxy.sln references:
  - src/AcmeProxy/AcmeProxy.csproj
  - tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj
```

---

## NuGet Dependencies

### `src/AcmeProxy/AcmeProxy.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<Nullable>enable</Nullable>
		<ImplicitUsings>enable</ImplicitUsings>
	</PropertyGroup>
	<ItemGroup>
		<PackageReference Include="Certes" Version="3.*" />
		<PackageReference Include="DnsClient" Version="1.*" />
		<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.*" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*" />
	</ItemGroup>
</Project>
```

### `tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<Nullable>enable</Nullable>
		<ImplicitUsings>enable</ImplicitUsings>
		<IsPackable>false</IsPackable>
	</PropertyGroup>
	<ItemGroup>
		<PackageReference Include="xunit" Version="2.*" />
		<PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
		<PackageReference Include="Moq" Version="4.*" />
		<PackageReference Include="FluentAssertions" Version="6.*" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.*" />
		<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*" />
		<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
	</ItemGroup>
	<ItemGroup>
		<ProjectReference Include="..\..\src\AcmeProxy\AcmeProxy.csproj" />
	</ItemGroup>
</Project>
```

---

## Configuration

### `appsettings.json`

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

### `appsettings.Development.json`

```json
{
	"ConnectionStrings": {
		"Default": "Data Source=acmeproxy-dev.db"
	},
	"Proxy": {
		"LetsEncrypt": {
			"UseStaging": true
		}
	}
}
```

---

## Strongly-Typed Configuration (`Configuration/ProxyOptions.cs`)

```csharp
public class ProxyOptions
{
	public const string Section = "Proxy";
	public List<string> AllowedDomains { get; set; } = new();
	public LetsEncryptOptions LetsEncrypt { get; set; } = new();
	public HestiaCPOptions HestiaCP { get; set; } = new();
	public DnsOptions Dns { get; set; } = new();
}

public class LetsEncryptOptions
{
	public bool UseStaging { get; set; } = true;
	public string AccountEmail { get; set; } = string.Empty;
}

public class HestiaCPOptions
{
	public string BaseUrl { get; set; } = string.Empty;
	public string AccessKey { get; set; } = string.Empty;
	public string SecretKey { get; set; } = string.Empty;
	public string Username { get; set; } = string.Empty;
}

public class DnsOptions
{
	public int PropagationPollIntervalSeconds { get; set; } = 10;
	public int PropagationTimeoutSeconds { get; set; } = 300;
	public List<string> ResolverAddresses { get; set; } = new();
}
```

---

## Database Entities (`Data/Entities/`)

### `LeAccount.cs`

```csharp
public class LeAccount
{
	public int Id { get; set; }
	public string Email { get; set; } = string.Empty;
	public string AccountKeyPem { get; set; } = string.Empty;   // certes-serialised account key
	public string AccountUri { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; }
}
```

### `ProxyOrder.cs`

```csharp
public class ProxyOrder
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string Domain { get; set; } = string.Empty;
	public string IdentifiersJson { get; set; } = "[]";          // JSON array of domain strings
	public string Status { get; set; } = "pending";              // pending|ready|processing|valid|invalid
	public string? LeOrderUrl { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime UpdatedAt { get; set; }
	public ProxyAuthorization? Authorization { get; set; }
	public ProxyCertificate? Certificate { get; set; }
}
```

### `ProxyAuthorization.cs`

```csharp
public class ProxyAuthorization
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string OrderId { get; set; } = string.Empty;
	public string Domain { get; set; } = string.Empty;
	public string Status { get; set; } = "pending";              // pending|valid|invalid
	public ProxyOrder Order { get; set; } = null!;
	public ProxyChallenge? Challenge { get; set; }
}
```

### `ProxyChallenge.cs`

```csharp
public class ProxyChallenge
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string AuthorizationId { get; set; } = string.Empty;
	public string Token { get; set; } = string.Empty;
	public string TxtValue { get; set; } = string.Empty;         // computed _acme-challenge TXT value
	public string Status { get; set; } = "pending";              // pending|processing|valid|invalid
	public string? HestiaDnsRecordId { get; set; }               // stored for later deletion
	public string? Error { get; set; }
	public ProxyAuthorization Authorization { get; set; } = null!;
}
```

### `ProxyCertificate.cs`

```csharp
public class ProxyCertificate
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string OrderId { get; set; } = string.Empty;
	public string CertificateChainPem { get; set; } = string.Empty;   // full LE-signed chain
	public DateTime IssuedAt { get; set; }
	public DateTime ExpiresAt { get; set; }
	public ProxyOrder Order { get; set; } = null!;
}
```

---

## DbContext (`Data/AcmeProxyDbContext.cs`)

```csharp
public class AcmeProxyDbContext : DbContext
{
	public DbSet<LeAccount> LeAccounts => Set<LeAccount>();
	public DbSet<ProxyOrder> Orders => Set<ProxyOrder>();
	public DbSet<ProxyAuthorization> Authorizations => Set<ProxyAuthorization>();
	public DbSet<ProxyChallenge> Challenges => Set<ProxyChallenge>();
	public DbSet<ProxyCertificate> Certificates => Set<ProxyCertificate>();

	public AcmeProxyDbContext(DbContextOptions<AcmeProxyDbContext> options) : base(options) { }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<ProxyOrder>()
			.HasOne(o => o.Authorization)
			.WithOne(a => a.Order)
			.HasForeignKey<ProxyAuthorization>(a => a.OrderId);

		modelBuilder.Entity<ProxyOrder>()
			.HasOne(o => o.Certificate)
			.WithOne(c => c.Order)
			.HasForeignKey<ProxyCertificate>(c => c.OrderId);

		modelBuilder.Entity<ProxyAuthorization>()
			.HasOne(a => a.Challenge)
			.WithOne(c => c.Authorization)
			.HasForeignKey<ProxyChallenge>(c => c.AuthorizationId);
	}
}
```

EF migrations must be generated prior to first run:

```bash
dotnet ef migrations add InitialCreate --project src/AcmeProxy
dotnet ef database update --project src/AcmeProxy
```

On startup, call `context.Database.MigrateAsync()` to apply pending migrations automatically.

---

## DNS Provider Abstraction (`Services/IDnsProviderPlugin.cs`)

```csharp
/// <summary>
/// Abstraction for DNS provider challenge fulfilment.
/// Returns a provider-specific record ID from AddTxtRecordAsync for use in DeleteTxtRecordAsync.
/// </summary>
public interface IDnsProviderPlugin
{
	Task<string> AddTxtRecordAsync(
		string domain,
		string recordName,
		string value,
		CancellationToken ct);

	Task DeleteTxtRecordAsync(
		string domain,
		string recordName,
		string recordId,
		CancellationToken ct);
}
```

---

## HestiaCP Client (`HestiaCP/HestiaCPClient.cs`)

Implements `IDnsProviderPlugin`.

### HestiaCP API Notes

HestiaCP Access Key API uses HTTP POST with `application/x-www-form-urlencoded` body:

```
POST https://panel:8083/api/v1/exec/
Body:
  cmd=v-add-dns-record
  accesskey=<AccessKey>
  secretkey=<SecretKey>
  arg1=<Username>
  arg2=<domain>         (e.g. example.com)
  arg3=<record-name>   (e.g. _acme-challenge)
  arg4=TXT
  arg5=<txt-value>
  arg6=60              (TTL in seconds — use low value for ACME)
```

Successful response body is `"0"`. Non-zero indicates error.

### `AddTxtRecordAsync` implementation steps

1. POST `v-add-dns-record` with above parameters
2. Assert response is `"0"`
3. POST `v-list-dns-records` with `arg1=<Username>`, `arg2=<domain>`
4. Parse JSON response to find the record matching `arg3=_acme-challenge` and `arg5=<value>`
5. Return the record's ID field as a string

### `DeleteTxtRecordAsync` implementation steps

1. POST `v-delete-dns-record` with `arg1=<Username>`, `arg2=<domain>`, `arg3=<recordId>`
2. Assert response is `"0"`
3. On failure, log warning but do not throw (best-effort cleanup)

### `HestiaCPOptions.cs`

```csharp
public class HestiaCPOptions
{
	public string BaseUrl { get; set; } = string.Empty;
	public string AccessKey { get; set; } = string.Empty;
	public string SecretKey { get; set; } = string.Empty;
	public string Username { get; set; } = string.Empty;
}
```

---

## DNS Propagation Poller (`Services/DnsPropagationPoller.cs`)

```csharp
public class DnsPropagationPoller
{
	// Dependencies: IOptions<ProxyOptions>, ILogger<DnsPropagationPoller>

	/// <summary>
	/// Polls all configured public DNS resolvers until the expected TXT value
	/// is observed on ALL resolvers, or the configured timeout is exceeded.
	/// Throws TimeoutException on timeout.
	/// </summary>
	public Task WaitForPropagationAsync(
		string fqdn,            // e.g. _acme-challenge.example.com
		string expectedValue,   // exact TXT value to match
		CancellationToken ct);
}
```

### Implementation Notes

- Use `DnsClient.LookupClient` initialised with explicit `IPAddress` resolver list from config
- Query `QueryType.TXT` for `fqdn`
- Strip surrounding quotes from returned TXT values before comparison
- Poll every `PropagationPollIntervalSeconds`
- Track elapsed time; throw `TimeoutException` if `PropagationTimeoutSeconds` exceeded
- Log each poll attempt at `Debug` level; log success at `Information` level

---

## Let's Encrypt Client (`LetsEncrypt/LetsEncryptClient.cs`)

Wraps `certes` (`Certes.Acme`).

### `LetsEncryptOptions.cs`

```csharp
public class LetsEncryptOptions
{
	public bool UseStaging { get; set; } = true;
	public string AccountEmail { get; set; } = string.Empty;
}
```

### Public Interface

```csharp
public class LetsEncryptClient
{
	// Dependencies: AcmeProxyDbContext, IOptions<ProxyOptions>, ILogger<LetsEncryptClient>

	/// <summary>
	/// Called once on startup. Loads LE account from DB or creates a new one.
	/// Must be called before any other method.
	/// </summary>
	public Task InitialiseAsync(CancellationToken ct);

	/// <summary>
	/// Creates a new ACME order on Let's Encrypt for the given identifiers.
	/// Returns the LE order URL, the DNS-01 token, and the computed TXT value.
	/// </summary>
	public Task<LeOrderContext> CreateOrderAsync(
		IEnumerable<string> identifiers,
		CancellationToken ct);

	/// <summary>
	/// Notifies LE that the DNS challenge has been set and is ready for validation.
	/// </summary>
	public Task NotifyChallengeReadyAsync(string leOrderUrl, CancellationToken ct);

	/// <summary>
	/// Polls LE until the challenge status reaches valid or invalid.
	/// Throws InvalidOperationException if LE reports invalid.
	/// </summary>
	public Task WaitForChallengeValidationAsync(string leOrderUrl, CancellationToken ct);

	/// <summary>
	/// Submits the client's CSR DER bytes to LE and retrieves the full certificate chain PEM.
	/// </summary>
	public Task<string> FinalizeOrderAsync(string leOrderUrl, byte[] csrDer, CancellationToken ct);
}

public record LeOrderContext(
	string LeOrderUrl,
	string Token,
	string DnsTxtValue
);
```

### Implementation Notes

- Use `WellKnownServers.LetsEncryptStagingV2` when `UseStaging = true`, else `WellKnownServers.LetsEncryptV2`
- Store `AcmeContext` as a private field after `InitialiseAsync`
- When loading an existing account, rehydrate via `AcmeContext.Account()` using the stored key PEM
- For `CreateOrderAsync`, retrieve the first `dns-01` challenge from the first authorization
- For `WaitForChallengeValidationAsync`, poll LE every 5 seconds up to 10 attempts before giving up

---

## Nonce Service (`Services/NonceService.cs`)

```csharp
public class NonceService
{
	// Maintains a ConcurrentQueue<string> of issued nonces
	// Each nonce is a cryptographically random Base64url string (16 bytes)

	/// <summary>Issues a new nonce and enqueues it for later validation.</summary>
	public string IssueNonce();

	/// <summary>
	/// Validates and consumes a nonce. Returns false if the nonce was not issued
	/// or has already been consumed.
	/// </summary>
	public bool ConsumeNonce(string nonce);
}
```

---

## Order Fulfilment Service (`Services/OrderFulfilmentService.cs`)

Orchestrates the full proxy challenge fulfilment flow. Called asynchronously from `AcmeController` when the client POSTs to `/acme/challenge/{id}`.

### Dependencies

- `AcmeProxyDbContext`
- `LetsEncryptClient`
- `IDnsProviderPlugin`
- `DnsPropagationPoller`
- `ILogger<OrderFulfilmentService>`

### Fulfilment Steps

```
1.  Load ProxyChallenge (include Authorization, include Order) from DB by challenge ID
2.  If challenge.Status != "pending", return early (idempotent)
3.  Update challenge.Status = "processing", save
4.  Call LetsEncryptClient.CreateOrderAsync(order.Identifiers)
    → receives (leOrderUrl, token, dnsTxtValue)
5.  Persist leOrderUrl to ProxyOrder, dnsTxtValue + token to ProxyChallenge, save
6.  Derive FQDN: "_acme-challenge." + authorization.Domain
7.  Call IDnsProviderPlugin.AddTxtRecordAsync(domain, "_acme-challenge", dnsTxtValue)
    → receives hestiaDnsRecordId
8.  Persist hestiaDnsRecordId to ProxyChallenge, save
9.  Call DnsPropagationPoller.WaitForPropagationAsync(fqdn, dnsTxtValue)
10. Call LetsEncryptClient.NotifyChallengeReadyAsync(leOrderUrl)
11. Call LetsEncryptClient.WaitForChallengeValidationAsync(leOrderUrl)
12. Call IDnsProviderPlugin.DeleteTxtRecordAsync(domain, "_acme-challenge", hestiaDnsRecordId)
    (best-effort — log error, do not throw)
13. Update challenge.Status = "valid"
14. Update authorization.Status = "valid"
15. Update order.Status = "ready", save
16. On any exception at steps 4–11:
    a. Attempt best-effort DNS record cleanup if hestiaDnsRecordId is set
    b. Update challenge.Status = "invalid", challenge.Error = exception.Message
    c. Update authorization.Status = "invalid"
    d. Update order.Status = "invalid", save
    e. Log error
```

### Domain Serialisation Lock

Use a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by domain to prevent concurrent fulfilment for the same domain. Acquire the semaphore at step 1, release in a `finally` block after step 16.

---

## ACME Controller (`Controllers/AcmeController.cs`)

### General RFC 8555 Compliance Requirements

- All ACME POST endpoints receive a JWS-signed request body (`application/jose+json`)
- Extract and validate the nonce from the JWS `protected` header via `NonceService.ConsumeNonce`
- Reject requests with invalid or consumed nonces with `400 Bad Request` + `urn:ietf:params:acme:error:badNonce`
- Include a fresh `Replay-Nonce` header on every response (issue via `NonceService.IssueNonce`)
- Include `Content-Type: application/json` on success responses
- Include `Content-Type: application/problem+json` on error responses
- All resource URLs in responses must be absolute (derive base URL from `HttpContext.Request`)
- JWS signature verification is **required** for production correctness; implement using `System.Security.Cryptography` or a JWS library. Do not skip verification.

### Endpoint Implementations

---

#### `GET /acme/directory`

No authentication required.

Response `200 OK`:
```json
{
	"newNonce":   "https://<host>/acme/new-nonce",
	"newAccount": "https://<host>/acme/new-account",
	"newOrder":   "https://<host>/acme/new-order",
	"revokeCert": "https://<host>/acme/revoke-cert",
	"keyChange":  "https://<host>/acme/key-change",
	"meta": {
		"termsOfService": "https://letsencrypt.org/documents/LE-SA-v1.3-September-21-2022.pdf"
	}
}
```

---

#### `HEAD /acme/new-nonce`

Issues a new nonce. No body.

Response `204 No Content` with `Replay-Nonce: <nonce>` header.

---

#### `GET /acme/new-nonce`

Issues a new nonce. No body.

Response `200 OK` with `Replay-Nonce: <nonce>` header.

---

#### `POST /acme/new-account`

Decode JWS. Validate nonce. Extract public key from JWS header.

For this implementation, accept all registrations without external account binding. Return a synthetic account object. Account state does not need to be persisted — this service does not manage client ACME accounts, only its own LE account.

Response `201 Created` with `Location: https://<host>/acme/account/<id>` header:
```json
{
	"status": "valid",
	"contact": [],
	"orders": "https://<host>/acme/orders/<id>"
}
```

---

#### `POST /acme/new-order`

Decode JWS. Validate nonce.

Parse identifiers from payload:
```json
{
	"identifiers": [
		{ "type": "dns", "value": "sub.example.com" }
	]
}
```

Validate each identifier value against `ProxyOptions.AllowedDomains` whitelist. The check must match the root domain — e.g. `AllowedDomains: ["example.com"]` permits `sub.example.com` and `*.example.com`. Reject non-matching identifiers with `403 Forbidden` + `urn:ietf:params:acme:error:rejectedIdentifier`.

Create and persist:
- `ProxyOrder` (status = "pending")
- `ProxyAuthorization` linked to order
- `ProxyChallenge` linked to authorization (generate token as `Base64Url(RandomBytes(32))`)

Response `201 Created` with `Location: https://<host>/acme/order/<orderId>` header:
```json
{
	"status": "pending",
	"identifiers": [
		{ "type": "dns", "value": "sub.example.com" }
	],
	"authorizations": [
		"https://<host>/acme/authz/<authzId>"
	],
	"finalize": "https://<host>/acme/order/<orderId>/finalize"
}
```

---

#### `GET /acme/order/{orderId}`

Load `ProxyOrder` from DB (include Authorization, Certificate). Return 404 if not found.

Response `200 OK`:
```json
{
	"status": "<order.Status>",
	"identifiers": [ { "type": "dns", "value": "<domain>" } ],
	"authorizations": [ "https://<host>/acme/authz/<authzId>" ],
	"finalize": "https://<host>/acme/order/<orderId>/finalize",
	"certificate": "https://<host>/acme/cert/<certId>"   // only present when status = "valid"
}
```

---

#### `GET /acme/authz/{authzId}`

Load `ProxyAuthorization` from DB (include Challenge). Return 404 if not found.

Response `200 OK`:
```json
{
	"status": "<authz.Status>",
	"identifier": { "type": "dns", "value": "<authz.Domain>" },
	"challenges": [
		{
			"type": "dns-01",
			"status": "<challenge.Status>",
			"url": "https://<host>/acme/challenge/<challengeId>",
			"token": "<challenge.Token>"
		}
	]
}
```

---

#### `POST /acme/challenge/{challengeId}`

Decode JWS. Validate nonce.

Load `ProxyChallenge` from DB. Return 404 if not found.

If `challenge.Status == "pending"`:
- Fire and forget `OrderFulfilmentService.FulfilAsync(challengeId)` as a background task
- Do not await — return immediately per RFC 8555

Response `200 OK`:
```json
{
	"type": "dns-01",
	"status": "processing",
	"url": "https://<host>/acme/challenge/<challengeId>",
	"token": "<challenge.Token>"
}
```

If `challenge.Status != "pending"`, return current state without re-triggering fulfilment.

---

#### `POST /acme/order/{orderId}/finalize`

Decode JWS. Validate nonce.

Load `ProxyOrder` from DB. Return 404 if not found.

If `order.Status != "ready"`, return `403 Forbidden` + `urn:ietf:params:acme:error:orderNotReady`.

Extract CSR DER bytes from JWS payload:
```json
{ "csr": "<Base64url-encoded DER>" }
```

Call `LetsEncryptClient.FinalizeOrderAsync(order.LeOrderUrl, csrDer)` → receives certificate chain PEM.

Create and persist `ProxyCertificate`. Update `order.Status = "valid"`, `order.CertificateId`. Save.

Response `200 OK`:
```json
{
	"status": "valid",
	"identifiers": [ { "type": "dns", "value": "<domain>" } ],
	"authorizations": [ "https://<host>/acme/authz/<authzId>" ],
	"finalize": "https://<host>/acme/order/<orderId>/finalize",
	"certificate": "https://<host>/acme/cert/<certId>"
}
```

---

#### `GET /acme/cert/{certId}`

Load `ProxyCertificate` from DB. Return 404 if not found.

Response `200 OK` with `Content-Type: application/pem-certificate-chain`:

```
<full PEM chain as returned by Let's Encrypt>
```

---

#### `POST /acme/revoke-cert`

Return `501 Not Implemented` for this version.

---

#### `POST /acme/key-change`

Return `501 Not Implemented` for this version.

---

## Domain Whitelist Validation Logic

Given `AllowedDomains: ["example.com", "other.org"]`, the following identifiers are permitted:

| Identifier | Permitted |
|---|---|
| `example.com` | Yes |
| `sub.example.com` | Yes |
| `deep.sub.example.com` | Yes |
| `*.example.com` | Yes |
| `other.org` | Yes |
| `evil.com` | No |
| `notexample.com` | No |

Implementation: for each requested identifier, extract the root domain (last two labels) and check membership in `AllowedDomains`. Wildcard identifiers (`*.example.com`) should strip the `*.` prefix before checking.

---

## `Program.cs` Startup Sequence

```csharp
// 1. Build configuration
// 2. Register services:
//    - AddDbContext<AcmeProxyDbContext> with SQLite
//    - AddOptions<ProxyOptions>().Bind(config.GetSection("Proxy"))
//    - AddHttpClient("hestiacp")
//    - AddSingleton<NonceService>
//    - AddSingleton<IDnsProviderPlugin, HestiaCPDnsProvider>
//    - AddScoped<DnsPropagationPoller>
//    - AddScoped<LetsEncryptClient>
//    - AddScoped<OrderFulfilmentService>
//    - AddControllers()
// 3. Build app
// 4. Run EF migrations: await context.Database.MigrateAsync()
// 5. Initialise LE account: await letsEncryptClient.InitialiseAsync()
// 6. Map controllers
// 7. Run
```

---

## Test Plan (`tests/AcmeProxy.Tests/`)

### Test Helpers

#### `Helpers/TestDbContextFactory.cs`

```csharp
// Creates an AcmeProxyDbContext backed by Microsoft.EntityFrameworkCore.InMemory
// Each test gets a unique database name (Guid) to ensure isolation
public static class TestDbContextFactory
{
	public static AcmeProxyDbContext Create();
}
```

#### `Helpers/AcmeJwsHelper.cs`

```csharp
// Generates valid JWS-signed ACME request bodies for use in controller tests
// Uses an in-memory RSA key pair
public static class AcmeJwsHelper
{
	public static string CreateSignedBody(string nonce, object payload);
}
```

---

### `Services/NonceServiceTests.cs`

| Test | Description |
|---|---|
| `IssueNonce_ReturnsNonEmptyString` | Issued nonce is not null or empty |
| `IssueNonce_ReturnsUniqueValues` | Two consecutive nonces are not equal |
| `ConsumeNonce_ReturnsTrueForIssuedNonce` | Valid nonce is consumed successfully |
| `ConsumeNonce_ReturnsFalseForUnknownNonce` | Unknown nonce returns false |
| `ConsumeNonce_ReturnsFalseForAlreadyConsumedNonce` | Nonce cannot be consumed twice |
| `ConsumeNonce_IsConcurrentlySafe` | Parallel consumption of the same nonce returns true exactly once |

---

### `Services/DnsPropagationPollerTests.cs`

Use `Moq` to mock `LookupClient` or inject a fake resolver.

| Test | Description |
|---|---|
| `WaitForPropagation_ReturnsWhenTxtValueObservedOnAllResolvers` | Succeeds when all resolvers return expected value |
| `WaitForPropagation_ThrowsTimeoutException_WhenValueNotObservedWithinTimeout` | Throws after configured timeout |
| `WaitForPropagation_RetriesUntilValueAppears` | Polls multiple times before succeeding |
| `WaitForPropagation_ThrowsOperationCanceledException_WhenCancelled` | Respects cancellation token |
| `WaitForPropagation_StripsQuotesFromTxtValues` | TXT values with surrounding quotes are matched correctly |
| `WaitForPropagation_RequiresAllResolversToConfirm` | Does not succeed if only one of two resolvers confirms |

---

### `HestiaCP/HestiaCPClientTests.cs`

Use `Moq` for `HttpMessageHandler` to intercept HTTP calls.

| Test | Description |
|---|---|
| `AddTxtRecord_PostsCorrectCommand` | Verifies `v-add-dns-record` is posted with correct parameters |
| `AddTxtRecord_ReturnsRecordId` | Returns record ID from `v-list-dns-records` response |
| `AddTxtRecord_ThrowsOnNonZeroResponse` | Throws on HestiaCP error response |
| `DeleteTxtRecord_PostsCorrectCommand` | Verifies `v-delete-dns-record` is posted with record ID |
| `DeleteTxtRecord_DoesNotThrowOnFailure` | Logs warning but does not throw on deletion failure |
| `AddTxtRecord_UsesLowTtl` | Verifies TTL of 60 seconds is sent |

---

### `LetsEncrypt/LetsEncryptClientTests.cs`

Use a mock `AcmeContext` (interface-based or wrapper) or target the LE staging environment with a fake ACME server (e.g. Pebble) for integration tests. For unit tests, mock certes interfaces.

| Test | Description |
|---|---|
| `Initialise_CreatesNewAccount_WhenNoneExists` | Creates and persists LE account if DB is empty |
| `Initialise_LoadsExistingAccount_WhenPresent` | Rehydrates certes context from stored PEM |
| `CreateOrder_ReturnsDnsTxtValue` | Returns expected token and TXT value |
| `NotifyChallengeReady_CallsLe` | Signals challenge ready without error |
| `WaitForValidation_Succeeds_WhenLeReturnsValid` | Completes when LE challenge is valid |
| `WaitForValidation_Throws_WhenLeReturnsInvalid` | Throws `InvalidOperationException` on LE invalid status |
| `FinalizeOrder_ReturnsCertificateChainPem` | Returns non-empty PEM on success |

---

### `Services/OrderFulfilmentServiceTests.cs`

Use `TestDbContextFactory` and Moq for `IDnsProviderPlugin`, `LetsEncryptClient`, `DnsPropagationPoller`.

| Test | Description |
|---|---|
| `Fulfil_SetsChallengeToProcesing_ThenValid` | Full happy path transitions challenge and order status correctly |
| `Fulfil_PersistsDnsTxtValue` | TXT value from LE is stored on challenge entity |
| `Fulfil_PersistsHestiaRecordId` | Record ID from DNS provider stored on challenge entity |
| `Fulfil_CleansDnsRecord_AfterValidation` | `DeleteTxtRecordAsync` called after LE confirms valid |
| `Fulfil_SetsStatusToInvalid_OnDnsPropagationTimeout` | Challenge and order set to invalid on timeout |
| `Fulfil_SetsStatusToInvalid_OnLeValidationFailure` | Challenge and order set to invalid when LE returns invalid |
| `Fulfil_CleansDnsRecord_EvenOnFailure` | DNS record cleanup attempted on failure path |
| `Fulfil_IsIdempotent_WhenChallengeAlreadyProcessing` | Does not re-trigger if already processing |
| `Fulfil_SerialisesConcurrentOrdersForSameDomain` | Concurrent fulfilment for same domain queues correctly |

---

### `Controllers/AcmeControllerTests.cs`

Use `Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory` with `TestServer`. Override services with mocks/in-memory implementations.

| Test | Description |
|---|---|
| `GetDirectory_Returns200_WithAllEndpoints` | Directory contains all required RFC 8555 URLs |
| `HeadNewNonce_Returns204_WithReplayNonceHeader` | Nonce header present on HEAD response |
| `GetNewNonce_Returns200_WithReplayNonceHeader` | Nonce header present on GET response |
| `PostNewAccount_Returns201_WithLocationHeader` | Account creation returns correct status and location |
| `PostNewOrder_Returns201_ForAllowedDomain` | Order created for whitelisted domain |
| `PostNewOrder_Returns403_ForDisallowedDomain` | Rejected identifier returns correct ACME error |
| `PostNewOrder_Returns403_ForWildcardOnDisallowedDomain` | Wildcard on non-whitelisted domain rejected |
| `PostNewOrder_Returns201_ForWildcardOnAllowedDomain` | Wildcard on whitelisted domain permitted |
| `PostNewOrder_Returns201_ForSubdomainOfAllowedDomain` | Subdomain of whitelisted root permitted |
| `GetOrder_Returns200_WithPendingStatus` | Newly created order returns pending |
| `GetOrder_Returns404_ForUnknownOrder` | Unknown order ID returns 404 |
| `GetAuthz_Returns200_WithDns01Challenge` | Authorization includes dns-01 challenge |
| `GetAuthz_Returns404_ForUnknownAuthz` | Unknown authz ID returns 404 |
| `PostChallenge_Returns200_AndTriggersBackground` | Challenge POST returns immediately with processing status |
| `PostChallenge_Returns400_ForBadNonce` | Invalid nonce returns badNonce error |
| `GetOrder_Returns200_WithReadyStatus_AfterFulfilment` | Order transitions to ready after background fulfilment |
| `PostFinalize_Returns200_WithCertificateUrl` | Finalize returns valid order with certificate URL |
| `PostFinalize_Returns403_WhenOrderNotReady` | Finalize on non-ready order returns orderNotReady error |
| `GetCert_Returns200_WithPemChain` | Certificate endpoint returns PEM content type and body |
| `GetCert_Returns404_ForUnknownCert` | Unknown cert ID returns 404 |
| `PostRevokeCert_Returns501` | Revoke returns not implemented |

---

### `Data/AcmeProxyDbContextTests.cs`

| Test | Description |
|---|---|
| `CanInsertAndRetrieveLeAccount` | Round-trip persist and load of `LeAccount` |
| `CanInsertAndRetrieveOrderWithAuthorizationAndChallenge` | Full entity graph persists and loads correctly |
| `CanInsertAndRetrieveCertificate` | `ProxyCertificate` linked to order persists correctly |
| `OrderStatus_UpdatesCorrectly` | Status field transitions persist |
| `ChallengeHestiaRecordId_IsNullable` | Null `HestiaDnsRecordId` persists without error |

---

## Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
VOLUME ["/data", "/plugins"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AcmeProxy.sln .
COPY src/AcmeProxy/AcmeProxy.csproj src/AcmeProxy/
COPY tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj tests/AcmeProxy.Tests/
RUN dotnet restore

COPY . .
RUN dotnet test tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj --no-restore --configuration Release

RUN dotnet publish src/AcmeProxy/AcmeProxy.csproj \
	--no-restore \
	--configuration Release \
	--output /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AcmeProxy.dll"]
```

Tests are run as part of the Docker build. The image will not be produced if tests fail.

---

## `docker-compose.yml`

```yaml
services:
  acmeproxy:
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "80:80"
    volumes:
      - acmeproxy-data:/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__Default=Data Source=/data/acmeproxy.db
      - Proxy__LetsEncrypt__UseStaging=true
      - Proxy__LetsEncrypt__AccountEmail=admin@example.com
      - Proxy__HestiaCP__BaseUrl=https://panel.example.com:8083
      - Proxy__HestiaCP__AccessKey=
      - Proxy__HestiaCP__SecretKey=
      - Proxy__HestiaCP__Username=
      - Proxy__AllowedDomains__0=example.com
    restart: unless-stopped

volumes:
  acmeproxy-data:
```

---

## Outstanding Risks and Mitigations

| Risk | Mitigation |
|---|---|
| HestiaCP `v-delete-dns-record` requires record ID not immediately available | Retrieve ID via `v-list-dns-records` immediately after `v-add-dns-record`; store on `ProxyChallenge` |
| DNS propagation timeout causes stuck orders | Order set to `invalid`; client must re-request; timeout is configurable |
| Concurrent orders for the same domain cause overlapping TXT records | Keyed `SemaphoreSlim` per domain in `OrderFulfilmentService` serialises fulfilment |
| LE account key lost if SQLite volume is unmounted | Document volume mount requirement; log clear error on startup if `/data` is not writable |
| JWS signature validation omitted | JWS signature verification must be implemented; do not accept unsigned or invalidly signed requests |
| certes API compatibility with .NET 10 | Verify certes NuGet package targets `netstandard2.0` or `net10.0`; if incompatible, evaluate `ACME-Client-NET` as alternative |
| `v-list-dns-records` response format | Verify HestiaCP JSON response structure against a live instance before implementing the record ID extraction logic |
| `dotnet/aspnet:10.0` base image availability | Confirm .NET 10 Docker images are available on MCR at time of build; use preview tag if GA is not yet released |

---

## Implementation Order (Recommended)

1. Solution and project scaffolding
2. Configuration models and `appsettings.json`
3. EF entities, `DbContext`, initial migration
4. `NonceService` + tests
5. `HestiaCPClient` + tests
6. `DnsPropagationPoller` + tests
7. `LetsEncryptClient` + tests
8. `IDnsProviderPlugin` interface, `HestiaCPDnsProvider` wrapper
9. `OrderFulfilmentService` + tests
10. ACME RFC 8555 models
11. `AcmeController` + tests
12. `Program.cs` wiring
13. Dockerfile + docker-compose
14. End-to-end smoke test against LE staging with a real certbot client
