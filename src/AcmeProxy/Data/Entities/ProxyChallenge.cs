namespace AcmeProxy.Data.Entities;

public class ProxyChallenge
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid AuthorizationId { get; set; }
	public string Token { get; set; } = string.Empty;
	public string TxtValue { get; set; } = string.Empty;         // computed _acme-challenge TXT value
	public string Status { get; set; } = "pending";              // pending|processing|valid|invalid
	public string? HestiaDnsRecordId { get; set; }               // stored for later deletion
	public string? Error { get; set; }
	public ProxyAuthorization Authorization { get; set; } = null!;
}
