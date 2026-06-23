using System.Net;
using AcmeProxy.Services;
using DnsClient;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// <see cref="IDnsTxtResolver"/> that queries the pebble-challtestsrv DNS server on a
/// specific port (8053), so the propagation poller can confirm the record the same way
/// Pebble's validation authority does.
/// </summary>
public class ChallTestSrvResolver : IDnsTxtResolver
{
	private readonly LookupClient _client;

	public ChallTestSrvResolver(int dnsPort)
		=> _client = new LookupClient(new NameServer(new IPEndPoint(IPAddress.Loopback, dnsPort)));

	public async Task<IReadOnlyList<string>> QueryTxtAsync(IPAddress resolver, string fqdn, CancellationToken ct)
	{
		var result = await _client.QueryAsync(fqdn, QueryType.TXT, cancellationToken: ct);
		return result.Answers.TxtRecords()
			.SelectMany(r => r.Text)
			.Select(t => t.Trim('"'))
			.ToList();
	}
}
