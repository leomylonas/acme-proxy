namespace AcmeProxy.LetsEncrypt;

public interface ILetsEncryptClient
{
	Task InitialiseAsync(CancellationToken ct);
	Task<LeOrderContext> CreateOrderAsync(IEnumerable<string> identifiers, CancellationToken ct);
	Task NotifyChallengeReadyAsync(string leOrderUrl, CancellationToken ct);
	Task WaitForChallengeValidationAsync(string leOrderUrl, CancellationToken ct);
	Task<string> FinalizeOrderAsync(string leOrderUrl, byte[] csrDer, CancellationToken ct);
}
