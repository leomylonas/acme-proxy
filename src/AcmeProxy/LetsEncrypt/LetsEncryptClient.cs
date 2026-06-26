using AcmeProxy.Configuration;
using AcmeProxy.Data;
using AcmeProxy.Data.Entities;
using Certes;
using Certes.Acme;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AcmeProxy.LetsEncrypt;

/// <summary>
/// Wraps the certes ACME client for outbound interaction with Let's Encrypt.
/// Registered as a singleton: the underlying <see cref="AcmeContext"/> (account + key) is
/// established once by <see cref="InitialiseAsync"/> and reused across all requests. The
/// (scoped) database context is obtained on demand via a service scope.
/// </summary>
public class LetsEncryptClient : ILetsEncryptClient
{
	/// <summary>Named <see cref="IHttpClientFactory"/> client carrying the HTTP logging handler.</summary>
	public const string HttpClientName = "letsencrypt";

	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly LetsEncryptOptions _options;
	private readonly ILogger<LetsEncryptClient> _logger;

	private AcmeContext? _acme;

	public LetsEncryptClient(
		IServiceScopeFactory scopeFactory,
		IHttpClientFactory httpClientFactory,
		IOptions<ProxyOptions> options,
		ILogger<LetsEncryptClient> logger)
	{
		_scopeFactory = scopeFactory;
		_httpClientFactory = httpClientFactory;
		_options = options.Value.LetsEncrypt;
		_logger = logger;
	}

	private Uri DirectoryUri => !string.IsNullOrWhiteSpace(_options.DirectoryUrl)
		? new Uri(_options.DirectoryUrl)
		: _options.UseStaging
			? WellKnownServers.LetsEncryptStagingV2
			: WellKnownServers.LetsEncryptV2;

	private IAcmeHttpClient CreateAcmeHttpClient() =>
		new AcmeHttpClient(DirectoryUri, _httpClientFactory.CreateClient(HttpClientName));

	private AcmeContext Acme => _acme
		?? throw new InvalidOperationException("LetsEncryptClient.InitialiseAsync must be called before use.");

	/// <summary>
	/// Called once on startup. Loads LE account from DB or creates a new one.
	/// </summary>
	public async Task InitialiseAsync(CancellationToken ct)
	{
		_logger.LogInformation("Initialising Let's Encrypt client against {Directory} (staging={Staging})",
			DirectoryUri, _options.UseStaging);

		using var scope = _scopeFactory.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<AcmeProxyDbContext>();

		var existing = await db.LeAccounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
		if (existing is not null)
		{
			var key = KeyFactory.FromPem(existing.AccountKeyPem);
			_acme = new AcmeContext(DirectoryUri, key, CreateAcmeHttpClient());
			await _acme.Account(); // rehydrate / verify
			_logger.LogInformation("Loaded existing Let's Encrypt account {Uri}", existing.AccountUri);
			return;
		}

		_logger.LogInformation("No stored Let's Encrypt account; registering a new one for {Email}", _options.AccountEmail);
		_acme = new AcmeContext(DirectoryUri, null, CreateAcmeHttpClient());
		var accountTask = _acme.NewAccount(_options.AccountEmail, termsOfServiceAgreed: true);
		await accountTask;
		var location = await accountTask.Location();
		var record = new LeAccount
		{
			Email = _options.AccountEmail,
			AccountKeyPem = _acme.AccountKey.ToPem(),
			AccountUri = location.ToString(),
			CreatedAt = DateTime.UtcNow,
		};
		db.LeAccounts.Add(record);
		await db.SaveChangesAsync(ct);
		_logger.LogInformation("Created new Let's Encrypt account {Uri}", record.AccountUri);
	}

	/// <summary>
	/// Creates a new ACME order on Let's Encrypt for the given identifiers.
	/// Returns the LE order URL, the DNS-01 token, and the computed TXT value.
	/// </summary>
	public async Task<LeOrderContext> CreateOrderAsync(
		IEnumerable<string> identifiers,
		CancellationToken ct)
	{
		var identifierList = identifiers.ToList();
		_logger.LogDebug("Creating LE order for [{Identifiers}]", string.Join(", ", identifierList));
		var order = await Acme.NewOrder(identifierList);
		var orderUrl = order.Location.ToString();

		var authorizations = await order.Authorizations();
		var authz = authorizations.First();
		var dnsChallenge = await authz.Dns()
			?? throw new InvalidOperationException("Let's Encrypt did not offer a dns-01 challenge.");

		var token = dnsChallenge.Token;
		var txtValue = Acme.AccountKey.DnsTxt(token);

		_logger.LogDebug("LE order {Url} created; dns-01 token={Token}", orderUrl, token);
		return new LeOrderContext(orderUrl, token, txtValue);
	}

	/// <summary>
	/// Notifies LE that the DNS challenge has been set and is ready for validation.
	/// </summary>
	public async Task NotifyChallengeReadyAsync(string leOrderUrl, CancellationToken ct)
	{
		_logger.LogDebug("Triggering LE validation for order {Url}", leOrderUrl);
		var dnsChallenge = await GetDnsChallengeAsync(leOrderUrl);
		await dnsChallenge.Validate();
	}

	/// <summary>
	/// Polls LE until the challenge status reaches valid or invalid.
	/// Throws InvalidOperationException if LE reports invalid.
	/// </summary>
	public async Task WaitForChallengeValidationAsync(string leOrderUrl, CancellationToken ct)
	{
		var dnsChallenge = await GetDnsChallengeAsync(leOrderUrl);

		for (var attempt = 0; attempt < 10; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			var resource = await dnsChallenge.Resource();
			_logger.LogDebug("LE challenge poll {Attempt}/10 for {Url}: status={Status}", attempt + 1, leOrderUrl, resource.Status);

			switch (resource.Status)
			{
				case Certes.Acme.Resource.ChallengeStatus.Valid:
					_logger.LogInformation("LE challenge for order {Url} is valid", leOrderUrl);
					return;
				case Certes.Acme.Resource.ChallengeStatus.Invalid:
					var detail = resource.Error?.Detail ?? "no detail";
					throw new InvalidOperationException($"Let's Encrypt reported the dns-01 challenge invalid: {detail}");
			}

			await Task.Delay(TimeSpan.FromSeconds(5), ct);
		}

		throw new TimeoutException($"Let's Encrypt did not validate the challenge for order '{leOrderUrl}' in time.");
	}

	/// <summary>
	/// Submits the client's CSR DER bytes to LE and retrieves the full certificate chain PEM.
	/// </summary>
	public async Task<string> FinalizeOrderAsync(string leOrderUrl, byte[] csrDer, CancellationToken ct)
	{
		_logger.LogDebug("Finalizing LE order {Url} with {Bytes}-byte CSR", leOrderUrl, csrDer.Length);
		var order = Acme.Order(new Uri(leOrderUrl));
		await order.Finalize(csrDer);

		// Real Let's Encrypt issues asynchronously: after finalize the order moves
		// pending/processing → valid, and only then is the certificate available. Poll until
		// it is valid before downloading (Pebble issues instantly, but LE does not).
		for (var attempt = 0; attempt < 15; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			var resource = await order.Resource();
			_logger.LogDebug("LE order finalize poll {Attempt}/15 for {Url}: status={Status}", attempt + 1, leOrderUrl, resource.Status);

			if (resource.Status == Certes.Acme.Resource.OrderStatus.Valid)
				break;
			if (resource.Status == Certes.Acme.Resource.OrderStatus.Invalid)
				throw new InvalidOperationException($"Let's Encrypt marked order '{leOrderUrl}' invalid during finalization.");

			await Task.Delay(TimeSpan.FromSeconds(2), ct);
		}

		var chain = await order.Download();

		// Concatenate exactly the certificates the ACME server returned (leaf + issuers).
		// We intentionally avoid chain.ToPem(), which resolves issuers against certes'
		// built-in CA store and throws for roots it does not know (e.g. Pebble's test root).
		var builder = new System.Text.StringBuilder();
		builder.AppendLine(chain.Certificate.ToPem().TrimEnd());
		foreach (var issuer in chain.Issuers)
			builder.AppendLine(issuer.ToPem().TrimEnd());
		var pem = builder.ToString();

		_logger.LogInformation("Downloaded certificate chain ({Bytes} bytes) for order {Url}", pem.Length, leOrderUrl);
		return pem;
	}

	private async Task<IChallengeContext> GetDnsChallengeAsync(string leOrderUrl)
	{
		var order = Acme.Order(new Uri(leOrderUrl));
		var authorizations = await order.Authorizations();
		var authz = authorizations.First();
		return await authz.Dns()
			?? throw new InvalidOperationException("Let's Encrypt did not offer a dns-01 challenge.");
	}
}
