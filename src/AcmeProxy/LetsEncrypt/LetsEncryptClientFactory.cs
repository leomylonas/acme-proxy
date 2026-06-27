using AcmeProxy.Configuration;
using Microsoft.Extensions.Options;

namespace AcmeProxy.LetsEncrypt;

/// <summary>
/// Holds one <see cref="LetsEncryptClient"/> per environment (staging / production) and
/// provides access by name. Registered as a singleton; both clients share the same lifetime
/// and are initialised once at startup via <see cref="InitialiseAllAsync"/>.
/// </summary>
public class LetsEncryptClientFactory : ILetsEncryptClientFactory
{
	private readonly ILetsEncryptClient _staging;
	private readonly ILetsEncryptClient _production;

	public LetsEncryptClientFactory(
		IServiceScopeFactory scopeFactory,
		IHttpClientFactory httpClientFactory,
		IOptions<ProxyOptions> options,
		ILoggerFactory loggerFactory)
	{
		_staging = new LetsEncryptClient(
			scopeFactory, httpClientFactory, options, isStaging: true,
			loggerFactory.CreateLogger<LetsEncryptClient>());

		_production = new LetsEncryptClient(
			scopeFactory, httpClientFactory, options, isStaging: false,
			loggerFactory.CreateLogger<LetsEncryptClient>());
	}

	public ILetsEncryptClient Get(string environment) =>
		environment == "production" ? _production : _staging;

	public Task InitialiseAllAsync(CancellationToken ct) =>
		Task.WhenAll(_staging.InitialiseAsync(ct), _production.InitialiseAsync(ct));
}
