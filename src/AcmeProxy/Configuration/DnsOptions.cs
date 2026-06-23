namespace AcmeProxy.Configuration;

public class DnsOptions
{
	public int PropagationPollIntervalSeconds { get; set; } = 10;
	public int PropagationTimeoutSeconds { get; set; } = 300;
	public List<string> ResolverAddresses { get; set; } = new();
}
