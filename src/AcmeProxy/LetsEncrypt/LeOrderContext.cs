namespace AcmeProxy.LetsEncrypt;

/// <summary>
/// The result of creating an order against Let's Encrypt: the order URL plus the
/// dns-01 challenge token and the TXT value that must be published.
/// </summary>
public record LeOrderContext(
	string LeOrderUrl,
	string Token,
	string DnsTxtValue);
