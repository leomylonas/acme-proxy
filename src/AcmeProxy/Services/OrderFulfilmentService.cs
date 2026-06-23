using System.Collections.Concurrent;
using AcmeProxy.Data;
using AcmeProxy.Data.Entities;
using AcmeProxy.LetsEncrypt;
using Microsoft.EntityFrameworkCore;

namespace AcmeProxy.Services;

/// <summary>
/// Orchestrates the full proxy DNS-01 challenge fulfilment flow against Let's Encrypt.
/// </summary>
public class OrderFulfilmentService
{
	private const string RecordName = "_acme-challenge";

	private static readonly ConcurrentDictionary<string, SemaphoreSlim> DomainLocks = new();

	private readonly AcmeProxyDbContext _db;
	private readonly ILetsEncryptClient _letsEncrypt;
	private readonly IDnsProviderPlugin _dnsProvider;
	private readonly IDnsPropagationPoller _poller;
	private readonly ILogger<OrderFulfilmentService> _logger;

	public OrderFulfilmentService(
		AcmeProxyDbContext db,
		ILetsEncryptClient letsEncrypt,
		IDnsProviderPlugin dnsProvider,
		IDnsPropagationPoller poller,
		ILogger<OrderFulfilmentService> logger)
	{
		_db = db;
		_letsEncrypt = letsEncrypt;
		_dnsProvider = dnsProvider;
		_poller = poller;
		_logger = logger;
	}

	public async Task FulfilAsync(Guid challengeId, CancellationToken ct = default)
	{
		_logger.LogDebug("Beginning fulfilment for challenge {Id}", challengeId);
		var challenge = await _db.Challenges
			.Include(c => c.Authorization)
				.ThenInclude(a => a.Order)
			.FirstOrDefaultAsync(c => c.Id == challengeId, ct);

		if (challenge is null)
		{
			_logger.LogWarning("Fulfilment requested for unknown challenge {Id}", challengeId);
			return;
		}

		if (challenge.Status != "pending")
		{
			_logger.LogInformation("Challenge {Id} is already {Status}; skipping fulfilment", challengeId, challenge.Status);
			return;
		}

		var authorization = challenge.Authorization;
		var order = authorization.Order;
		var domain = authorization.Domain;

		_logger.LogDebug("Acquiring domain lock for {Domain}", domain);
		var gate = DomainLocks.GetOrAdd(domain, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct);
		try
		{
			challenge.Status = "processing";
			await _db.SaveChangesAsync(ct);

			var identifiers = DeserialiseIdentifiers(order.IdentifiersJson, domain);
			_logger.LogInformation("Creating Let's Encrypt order for [{Identifiers}]", string.Join(", ", identifiers));
			var leOrder = await _letsEncrypt.CreateOrderAsync(identifiers, ct);
			_logger.LogDebug("LE order created: url={Url} token={Token} txt={Txt}", leOrder.LeOrderUrl, leOrder.Token, leOrder.DnsTxtValue);

			order.LeOrderUrl = leOrder.LeOrderUrl;
			order.UpdatedAt = DateTime.UtcNow;
			challenge.Token = leOrder.Token;
			challenge.TxtValue = leOrder.DnsTxtValue;
			await _db.SaveChangesAsync(ct);

			var fqdn = $"{RecordName}.{domain}";
			_logger.LogInformation("Adding TXT record {Fqdn} = {Value} via DNS provider", fqdn, leOrder.DnsTxtValue);
			challenge.HestiaDnsRecordId = await _dnsProvider.AddTxtRecordAsync(domain, RecordName, leOrder.DnsTxtValue, ct);
			await _db.SaveChangesAsync(ct);
			_logger.LogDebug("DNS provider returned record id {RecordId}", challenge.HestiaDnsRecordId);

			_logger.LogInformation("Waiting for DNS propagation of {Fqdn}", fqdn);
			await _poller.WaitForPropagationAsync(fqdn, leOrder.DnsTxtValue, ct);

			_logger.LogInformation("Notifying Let's Encrypt that challenge is ready");
			await _letsEncrypt.NotifyChallengeReadyAsync(leOrder.LeOrderUrl, ct);

			_logger.LogInformation("Waiting for Let's Encrypt challenge validation");
			await _letsEncrypt.WaitForChallengeValidationAsync(leOrder.LeOrderUrl, ct);

			await CleanupDnsRecordAsync(domain, challenge.HestiaDnsRecordId, ct);

			challenge.Status = "valid";
			authorization.Status = "valid";
			order.Status = "ready";
			order.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);

			_logger.LogInformation("Challenge {Id} for {Domain} fulfilled; order {Order} is ready", challengeId, domain, order.Id);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Fulfilment failed for challenge {Id} ({Domain})", challengeId, domain);

			await CleanupDnsRecordAsync(domain, challenge.HestiaDnsRecordId, ct);

			challenge.Status = "invalid";
			challenge.Error = ex.Message;
			authorization.Status = "invalid";
			order.Status = "invalid";
			order.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task CleanupDnsRecordAsync(string domain, string? recordId, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(recordId))
			return;

		// DeleteTxtRecordAsync is itself best-effort, but guard against cancellation propagation.
		try
		{
			await _dnsProvider.DeleteTxtRecordAsync(domain, RecordName, recordId, ct);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Best-effort DNS cleanup failed for {Domain} record {Id}", domain, recordId);
		}
	}

	private static List<string> DeserialiseIdentifiers(string json, string fallbackDomain)
	{
		try
		{
			var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
			if (list is { Count: > 0 })
				return list;
		}
		catch (System.Text.Json.JsonException)
		{
			// fall through to fallback
		}
		return new List<string> { fallbackDomain };
	}
}
