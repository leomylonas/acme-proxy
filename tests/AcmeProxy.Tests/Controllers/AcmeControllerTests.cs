using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AcmeProxy.Data.Entities;
using AcmeProxy.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace AcmeProxy.Tests.Controllers;

public class AcmeControllerTests : IClassFixture<AcmeWebApplicationFactory>
{
	private readonly AcmeWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public AcmeControllerTests(AcmeWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	private async Task<string> GetNonceAsync()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/new-nonce");
		return response.Headers.GetValues("Replay-Nonce").First();
	}

	private static HttpContent Jose(string body) =>
		new StringContent(body, Encoding.UTF8, "application/jose+json");

	private async Task<HttpResponseMessage> PostSignedAsync(string path, object? payload)
	{
		var nonce = await GetNonceAsync();
		var body = AcmeJwsHelper.CreateSignedBody(nonce, payload);
		return await _client.PostAsync(path, Jose(body));
	}

	private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
	{
		var text = await response.Content.ReadAsStringAsync();
		return JsonDocument.Parse(text).RootElement.Clone();
	}

	[Fact]
	public async Task GetDirectory_Returns200_WithAllEndpoints()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/directory");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var json = await JsonAsync(response);
		json.GetProperty("newNonce").GetString().Should().Contain("/letsencrypt/staging/new-nonce");
		json.GetProperty("newAccount").GetString().Should().Contain("/letsencrypt/staging/new-account");
		json.GetProperty("newOrder").GetString().Should().Contain("/letsencrypt/staging/new-order");
		json.GetProperty("revokeCert").GetString().Should().Contain("/letsencrypt/staging/revoke-cert");
		json.GetProperty("keyChange").GetString().Should().Contain("/letsencrypt/staging/key-change");
	}

	[Fact]
	public async Task HeadNewNonce_Returns204_WithReplayNonceHeader()
	{
		var request = new HttpRequestMessage(HttpMethod.Head, "/letsencrypt/staging/new-nonce");
		var response = await _client.SendAsync(request);
		response.StatusCode.Should().Be(HttpStatusCode.NoContent);
		response.Headers.Contains("Replay-Nonce").Should().BeTrue();
	}

	[Fact]
	public async Task GetNewNonce_Returns200_WithReplayNonceHeader()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/new-nonce");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.Contains("Replay-Nonce").Should().BeTrue();
	}

	[Fact]
	public async Task PostNewAccount_Returns201_WithLocationHeader()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-account", new { termsOfServiceAgreed = true });
		response.StatusCode.Should().Be(HttpStatusCode.Created);
		response.Headers.Location.Should().NotBeNull();
	}

	private static object NewOrder(string value) => new { identifiers = new[] { new { type = "dns", value } } };

	[Fact]
	public async Task PostNewOrder_Returns201_ForAllowedDomain()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("sub.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task PostNewOrder_Returns403_ForDisallowedDomain()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("evil.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await JsonAsync(response)).GetProperty("type").GetString().Should().Contain("rejectedIdentifier");
	}

	[Fact]
	public async Task PostNewOrder_Returns403_ForWildcardOnDisallowedDomain()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("*.evil.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task PostNewOrder_Returns201_ForWildcardOnAllowedDomain()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("*.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task PostNewOrder_Returns201_ForSubdomainOfAllowedDomain()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("deep.sub.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task GetOrder_Returns200_WithPendingStatus()
	{
		var created = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("sub.example.com"));
		var orderUrl = created.Headers.Location!.ToString();

		var response = await _client.GetAsync(orderUrl);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await JsonAsync(response)).GetProperty("status").GetString().Should().Be("pending");
	}

	[Fact]
	public async Task GetOrder_Returns404_ForUnknownOrder()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/order/does-not-exist");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task GetAuthz_Returns200_WithDns01Challenge()
	{
		var created = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("authz.example.com"));
		var order = await JsonAsync(created);
		var authzUrl = order.GetProperty("authorizations")[0].GetString()!;

		var response = await _client.GetAsync(authzUrl);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var json = await JsonAsync(response);
		json.GetProperty("challenges")[0].GetProperty("type").GetString().Should().Be("dns-01");
	}

	[Fact]
	public async Task GetAuthz_Returns404_ForUnknownAuthz()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/authz/nope");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	private async Task<string> ChallengeUrlForNewOrderAsync(string domain)
	{
		var created = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder(domain));
		var authzUrl = (await JsonAsync(created)).GetProperty("authorizations")[0].GetString()!;
		var authz = await JsonAsync(await _client.GetAsync(authzUrl));
		return authz.GetProperty("challenges")[0].GetProperty("url").GetString()!;
	}

	[Fact]
	public async Task PostChallenge_Returns200_AndTriggersBackground()
	{
		var challengeUrl = await ChallengeUrlForNewOrderAsync("trigger.example.com");
		var response = await PostSignedAsync(challengeUrl, new { });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await JsonAsync(response)).GetProperty("status").GetString().Should().BeOneOf("processing", "valid");
	}

	[Fact]
	public async Task PostChallenge_Returns400_ForBadNonce()
	{
		var challengeUrl = await ChallengeUrlForNewOrderAsync("badnonce.example.com");
		var body = AcmeJwsHelper.CreateSignedBody("totally-invalid-nonce", new { });
		var response = await _client.PostAsync(challengeUrl, Jose(body));
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await JsonAsync(response)).GetProperty("type").GetString().Should().Contain("badNonce");
	}

	[Fact]
	public async Task GetOrder_Returns200_WithReadyStatus_AfterFulfilment()
	{
		var created = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("ready.example.com"));
		var orderUrl = created.Headers.Location!.ToString();
		var authzUrl = (await JsonAsync(created)).GetProperty("authorizations")[0].GetString()!;
		var challengeUrl = (await JsonAsync(await _client.GetAsync(authzUrl)))
			.GetProperty("challenges")[0].GetProperty("url").GetString()!;

		await PostSignedAsync(challengeUrl, new { });

		string? status = null;
		for (var i = 0; i < 50; i++)
		{
			status = (await JsonAsync(await _client.GetAsync(orderUrl))).GetProperty("status").GetString();
			if (status == "ready") break;
			await Task.Delay(100);
		}
		status.Should().Be("ready");
	}

	[Fact]
	public async Task PostFinalize_Returns200_WithCertificateUrl()
	{
		var (orderId, _) = SeedReadyOrder("finalize.example.com");
		var csr = Convert.ToBase64String(new byte[] { 1, 2, 3 }).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		var response = await PostSignedAsync($"/letsencrypt/staging/order/{orderId}/finalize", new { csr });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var json = await JsonAsync(response);
		json.GetProperty("status").GetString().Should().Be("valid");
		json.GetProperty("certificate").GetString().Should().Contain("/letsencrypt/staging/cert/");
	}

	[Fact]
	public async Task PostFinalize_Returns403_WhenOrderNotReady()
	{
		var created = await PostSignedAsync("/letsencrypt/staging/new-order", NewOrder("notready.example.com"));
		var orderId = created.Headers.Location!.ToString().Split('/').Last();
		var csr = Convert.ToBase64String(new byte[] { 1 }).TrimEnd('=');

		var response = await PostSignedAsync($"/letsencrypt/staging/order/{orderId}/finalize", new { csr });
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await JsonAsync(response)).GetProperty("type").GetString().Should().Contain("orderNotReady");
	}

	[Fact]
	public async Task GetCert_Returns200_WithPemChain()
	{
		var (_, certId) = SeedCertificate("cert.example.com");
		var response = await _client.GetAsync($"/letsencrypt/staging/cert/{certId}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/pem-certificate-chain");
		(await response.Content.ReadAsStringAsync()).Should().Contain("BEGIN CERTIFICATE");
	}

	[Fact]
	public async Task GetCert_Returns404_ForUnknownCert()
	{
		var response = await _client.GetAsync("/letsencrypt/staging/cert/nope");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task PostRevokeCert_Returns501()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/revoke-cert", new { });
		response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
	}

	private async Task<string> RegisterAccountAsync()
	{
		var response = await PostSignedAsync("/letsencrypt/staging/new-account", new { termsOfServiceAgreed = true });
		response.StatusCode.Should().Be(HttpStatusCode.Created);
		return response.Headers.Location!.ToString();
	}

	private async Task<HttpResponseMessage> PostKidAsync(System.Security.Cryptography.RSA key, string kid, string path, object? payload)
	{
		var nonce = await GetNonceAsync();
		var body = AcmeJwsHelper.CreateKidSignedBody(key, nonce, kid, payload);
		return await _client.PostAsync(path, Jose(body));
	}

	[Fact]
	public async Task PostNewOrder_Returns201_ForKidSignedRequest_AfterRegistration()
	{
		var kid = await RegisterAccountAsync();
		var response = await PostKidAsync(AcmeJwsHelper.AccountKey, kid, "/letsencrypt/staging/new-order", NewOrder("kid.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task PostNewOrder_Returns400_AccountDoesNotExist_ForUnknownKid()
	{
		var unknownKid = $"{_client.BaseAddress}acme/account/{Guid.NewGuid():N}";
		var response = await PostKidAsync(AcmeJwsHelper.AccountKey, unknownKid, "/letsencrypt/staging/new-order", NewOrder("nokid.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await JsonAsync(response)).GetProperty("type").GetString().Should().Contain("accountDoesNotExist");
	}

	[Fact]
	public async Task PostNewOrder_Returns401_ForKidSignedWithWrongKey()
	{
		var kid = await RegisterAccountAsync();
		using var wrongKey = System.Security.Cryptography.RSA.Create(2048);
		var response = await PostKidAsync(wrongKey, kid, "/letsencrypt/staging/new-order", NewOrder("wrongkey.example.com"));
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await JsonAsync(response)).GetProperty("type").GetString().Should().Contain("unauthorized");
	}

	private (Guid orderId, Guid authzId) SeedReadyOrder(string domain)
	{
		using var db = _factory.CreateDbContext();
		var order = new ProxyOrder
		{
			Domain = domain,
			IdentifiersJson = $"[\"{domain}\"]",
			Status = "ready",
			LeOrderUrl = "https://le/order/1",
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
		};
		var authz = new ProxyAuthorization { OrderId = order.Id, Domain = domain, Status = "valid" };
		var challenge = new ProxyChallenge { AuthorizationId = authz.Id, Token = "t", Status = "valid" };
		db.AddRange(order, authz, challenge);
		db.SaveChanges();
		return (order.Id, authz.Id);
	}

	private (Guid orderId, Guid certId) SeedCertificate(string domain)
	{
		using var db = _factory.CreateDbContext();
		var order = new ProxyOrder { Domain = domain, Status = "valid", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		var cert = new ProxyCertificate
		{
			OrderId = order.Id,
			CertificateChainPem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
			IssuedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddDays(90),
		};
		db.AddRange(order, cert);
		db.SaveChanges();
		return (order.Id, cert.Id);
	}
}
