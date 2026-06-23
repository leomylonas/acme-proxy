using System.Net.Http.Json;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Thin client for the pebble-challtestsrv management API, used to publish/clear the
/// TXT records that Pebble queries when validating a dns-01 challenge.
/// </summary>
public class ChallTestSrvClient
{
	private readonly HttpClient _http;

	public ChallTestSrvClient(string managementBaseUrl)
		=> _http = new HttpClient { BaseAddress = new Uri(managementBaseUrl) };

	public async Task SetTxtAsync(string host, string value, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/set-txt", new { host, value }, ct);
		response.EnsureSuccessStatusCode();
	}

	public async Task ClearTxtAsync(string host, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/clear-txt", new { host }, ct);
		response.EnsureSuccessStatusCode();
	}
}
