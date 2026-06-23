using AcmeProxy.Services;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// <see cref="IDnsProviderPlugin"/> that publishes challenge TXT records into
/// pebble-challtestsrv (standing in for HestiaCP during Pebble e2e tests).
/// The returned record id is the fully-qualified host name used to clear it.
/// </summary>
public class ChallTestSrvDnsProvider : IDnsProviderPlugin
{
	private readonly ChallTestSrvClient _client;

	public ChallTestSrvDnsProvider(ChallTestSrvClient client) => _client = client;

	public async Task<string> AddTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct)
	{
		var host = $"{recordName}.{domain}.";
		await _client.SetTxtAsync(host, value, ct);
		return host;
	}

	public async Task DeleteTxtRecordAsync(string domain, string recordName, string recordId, CancellationToken ct)
		=> await _client.ClearTxtAsync(recordId, ct);
}
