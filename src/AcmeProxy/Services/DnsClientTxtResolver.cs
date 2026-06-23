using System.Net;
using DnsClient;

namespace AcmeProxy.Services;

/// <summary>
/// <see cref="IDnsTxtResolver"/> backed by DnsClient.NET, querying a specific resolver.
/// </summary>
public class DnsClientTxtResolver : IDnsTxtResolver
{
	public async Task<IReadOnlyList<string>> QueryTxtAsync(IPAddress resolver, string fqdn, CancellationToken ct)
	{
		var client = new LookupClient(resolver);
		var result = await client.QueryAsync(fqdn, QueryType.TXT, cancellationToken: ct);
		return result.Answers.TxtRecords()
			.SelectMany(r => r.Text)
			.Select(t => t.Trim('"'))
			.ToList();
	}
}
