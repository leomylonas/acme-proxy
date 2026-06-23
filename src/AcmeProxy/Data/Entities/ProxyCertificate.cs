namespace AcmeProxy.Data.Entities;

public class ProxyCertificate
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid OrderId { get; set; }
	public string CertificateChainPem { get; set; } = string.Empty;   // full LE-signed chain
	public DateTime IssuedAt { get; set; }
	public DateTime ExpiresAt { get; set; }
	public ProxyOrder Order { get; set; } = null!;
}
