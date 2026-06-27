namespace AcmeProxy.Data.Entities;

public class LeAccount
{
	public int Id { get; set; }
	public string Email { get; set; } = string.Empty;
	public string AccountKeyPem { get; set; } = string.Empty;   // certes-serialised account key
	public string AccountUri { get; set; } = string.Empty;
	public string Environment { get; set; } = "staging";   // "staging" or "production"
	public DateTime CreatedAt { get; set; }
}
