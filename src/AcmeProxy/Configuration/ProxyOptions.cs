namespace AcmeProxy.Configuration;

public class ProxyOptions
{
	public const string Section = "Proxy";
	public List<string> AllowedDomains { get; set; } = new();
	public LetsEncryptOptions LetsEncrypt { get; set; } = new();
	public HestiaCPOptions HestiaCP { get; set; } = new();
	public DnsOptions Dns { get; set; } = new();
}
