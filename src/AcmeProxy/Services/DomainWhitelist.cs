namespace AcmeProxy.Services;

/// <summary>
/// Validates requested ACME identifiers against a configured allow-list of root domains.
/// </summary>
public static class DomainWhitelist
{
	public static bool IsAllowed(string identifier, IEnumerable<string> allowedDomains)
	{
		if (string.IsNullOrWhiteSpace(identifier))
			return false;

		// Strip a leading wildcard label before matching.
		var candidate = identifier.StartsWith("*.", StringComparison.Ordinal)
			? identifier[2..]
			: identifier;

		var root = ExtractRootDomain(candidate);

		foreach (var allowed in allowedDomains)
		{
			if (string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(root, allowed, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static string ExtractRootDomain(string host)
	{
		var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (labels.Length <= 2)
			return host;
		return string.Join('.', labels[^2], labels[^1]);
	}
}
