using System.Net;

namespace AcmeProxy.Services;

/// <summary>
/// Abstraction over TXT-record resolution to allow the propagation poller to be
/// tested without performing real network DNS queries.
/// </summary>
public interface IDnsTxtResolver
{
	/// <summary>
	/// Queries the given resolver for TXT records at <paramref name="fqdn"/>.
	/// Returned values have surrounding quotes stripped.
	/// </summary>
	Task<IReadOnlyList<string>> QueryTxtAsync(IPAddress resolver, string fqdn, CancellationToken ct);
}
