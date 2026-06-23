namespace AcmeProxy.Data.Entities;

public class ProxyAuthorization
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid OrderId { get; set; }
	public string Domain { get; set; } = string.Empty;
	public string Status { get; set; } = "pending";              // pending|valid|invalid
	public ProxyOrder Order { get; set; } = null!;
	public ProxyChallenge? Challenge { get; set; }
}
