using AcmeProxy.E2ETests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AcmeProxy.E2ETests;

/// <summary>
/// Full ACME issuance flow driven through AcmeProxy's HTTP API, with the outbound ACME
/// client talking to a real Pebble server and the dns-01 challenge fulfilled via
/// challtestsrv. Exercises the certes order/validate/finalize path end-to-end.
///
/// Runs only when ACMEPROXY_E2E=1 (and Docker is available); otherwise skipped.
/// </summary>
[Collection(PebbleCollection.Name)]
public class PebbleEndToEndTests
{
	private const string Domain = "e2e.example.com";

	private readonly PebbleFixture _fixture;

	public PebbleEndToEndTests(PebbleFixture fixture) => _fixture = fixture;

	[SkippableFact]
	public async Task FullIssuanceFlow_ProducesCertificate()
	{
		Skip.IfNot(_fixture.Enabled, "Set ACMEPROXY_E2E=1 (with Docker available) to run Pebble e2e tests.");

		// Build the host only once we know Pebble is up (the fixture started it).
		using var factory = new PebbleE2EFactory();
		using var client = factory.CreateClient();
		var signer = new JwsSigner();

		string pem;
		try
		{
			pem = await AcmeIssuanceFlow.RunAsync(client, signer, Domain, TimeSpan.FromSeconds(90));
		}
		catch (Exception ex)
		{
			await using var db = factory.CreateDbContext();
			var rows = db.Challenges.Select(c => $"{c.Status}: {c.Error ?? "(no error)"}").ToList();
			throw new Xunit.Sdk.XunitException($"Flow failed: {ex.Message}\nChallenge rows: {string.Join(" | ", rows)}");
		}

		pem.Should().Contain("BEGIN CERTIFICATE", "Pebble should have issued a real (test) certificate");
	}
}
