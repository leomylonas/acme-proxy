namespace AcmeProxy.Services;

/// <summary>
/// Polls public DNS resolvers until a challenge TXT record has propagated.
/// </summary>
public interface IDnsPropagationPoller
{
	Task WaitForPropagationAsync(string fqdn, string expectedValue, CancellationToken ct);
}
