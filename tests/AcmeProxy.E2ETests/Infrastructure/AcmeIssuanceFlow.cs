using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Drives AcmeProxy's ACME HTTP API through a complete issuance for a single domain and
/// returns the issued PEM chain. Shared by the Pebble and live-staging e2e tests.
/// </summary>
public static class AcmeIssuanceFlow
{
	public static async Task<string> RunAsync(HttpClient client, JwsSigner signer, string domain, TimeSpan readyTimeout)
	{
		var account = await PostAsync(client, "/letsencrypt/staging/new-account",
			signer.SignWithJwk(await NonceAsync(client), new { termsOfServiceAgreed = true }));
		account.StatusCode.Should().Be(HttpStatusCode.Created);
		var kid = account.Headers.Location!.ToString();

		var orderResponse = await PostAsync(client, "/letsencrypt/staging/new-order",
			signer.SignWithKid(await NonceAsync(client), kid,
				new { identifiers = new[] { new { type = "dns", value = domain } } }));
		orderResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		var orderUrl = orderResponse.Headers.Location!.ToString();
		var authzUrl = (await JsonAsync(orderResponse)).GetProperty("authorizations")[0].GetString()!;

		var authz = await JsonAsync(await client.GetAsync(authzUrl));
		var challengeUrl = authz.GetProperty("challenges")[0].GetProperty("url").GetString()!;

		var challenge = await PostAsync(client, challengeUrl, signer.SignWithKid(await NonceAsync(client), kid, new { }));
		challenge.StatusCode.Should().Be(HttpStatusCode.OK);

		var status = await PollOrderStatusAsync(client, orderUrl, readyTimeout);
		status.Should().Be("ready", "the dns-01 challenge should be validated end-to-end");

		var csr = CsrHelper.Base64Url(CsrHelper.CreateCsrDer(domain));
		var finalize = await PostAsync(client, $"{orderUrl}/finalize",
			signer.SignWithKid(await NonceAsync(client), kid, new { csr }));
		finalize.StatusCode.Should().Be(HttpStatusCode.OK,
			"finalize body: {0}", await finalize.Content.ReadAsStringAsync());
		var finalized = await JsonAsync(finalize);
		finalized.GetProperty("status").GetString().Should().Be("valid");
		var certUrl = finalized.GetProperty("certificate").GetString()!;

		var cert = await client.GetAsync(certUrl);
		cert.StatusCode.Should().Be(HttpStatusCode.OK);
		return await cert.Content.ReadAsStringAsync();
	}

	private static async Task<string> NonceAsync(HttpClient client)
	{
		var response = await client.GetAsync("/letsencrypt/staging/new-nonce");
		return response.Headers.GetValues("Replay-Nonce").First();
	}

	private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string jwsBody) =>
		client.PostAsync(path, new StringContent(jwsBody, Encoding.UTF8, "application/jose+json"));

	private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

	private static async Task<string?> PollOrderStatusAsync(HttpClient client, string orderUrl, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		string? status = null;
		while (DateTime.UtcNow < deadline)
		{
			status = (await JsonAsync(await client.GetAsync(orderUrl))).GetProperty("status").GetString();
			if (status is "ready" or "invalid")
				break;
			await Task.Delay(TimeSpan.FromSeconds(2));
		}
		return status;
	}
}
