namespace AcmeProxy.LetsEncrypt;

public interface ILetsEncryptClientFactory
{
	ILetsEncryptClient Get(string environment);
	Task InitialiseAllAsync(CancellationToken ct);
}
