using AcmeProxy.E2ETests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AcmeProxy.E2ETests;

/// <summary>
/// Full issuance against the real Let's Encrypt <b>staging</b> directory using the real
/// HestiaCP DNS provider and public-resolver propagation. Requires live infrastructure and
/// is gated behind ACMEPROXY_E2E_STAGING=1 plus the E2E_DOMAIN / E2E_HESTIA_* variables.
///
/// The domain should be the apex of the HestiaCP-managed zone (or a wildcard of it), because
/// the challenge TXT is placed at _acme-challenge of that zone.
/// </summary>
public class LiveStagingEndToEndTests
{
	[SkippableFact]
	public async Task FullIssuanceFlow_AgainstLetsEncryptStaging()
	{
		var settings = StagingE2EFactory.Settings;
		Skip.IfNot(settings.Enabled, "Set ACMEPROXY_E2E_STAGING=1 to run the live staging e2e test.");
		Skip.IfNot(settings.IsComplete, "Live staging e2e requires E2E_DOMAIN and E2E_HESTIA_* environment variables.");

		using var factory = new StagingE2EFactory();
		using var client = factory.CreateClient();
		var signer = new JwsSigner();

		// Real LE staging validation + DNS propagation can take minutes.
		string pem;
		try
		{
			pem = await AcmeIssuanceFlow.RunAsync(client, signer, settings.Domain, TimeSpan.FromMinutes(6));
		}
		catch (Exception ex)
		{
			await using var db = factory.CreateDbContext();
			var rows = db.Challenges.Select(c => $"{c.Status}: {c.Error ?? "(no error)"}").ToList();
			throw new Xunit.Sdk.XunitException($"Flow failed: {ex.Message}\nChallenge rows: {string.Join(" | ", rows)}");
		}

		pem.Should().Contain("BEGIN CERTIFICATE");
	}
}
