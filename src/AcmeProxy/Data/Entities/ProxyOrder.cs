namespace AcmeProxy.Data.Entities;

public class ProxyOrder
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string Domain { get; set; } = string.Empty;
	public string IdentifiersJson { get; set; } = "[]";          // JSON array of domain strings
	public string Status { get; set; } = "pending";              // pending|ready|processing|valid|invalid
	public string LetsEncryptEnvironment { get; set; } = "staging";  // "staging" or "production"
	public string? LeOrderUrl { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime UpdatedAt { get; set; }
	public ProxyAuthorization? Authorization { get; set; }
	public ProxyCertificate? Certificate { get; set; }
}
