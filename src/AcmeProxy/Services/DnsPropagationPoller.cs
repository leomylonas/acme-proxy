using System.Diagnostics;
using System.Net;
using AcmeProxy.Configuration;
using Microsoft.Extensions.Options;

namespace AcmeProxy.Services;

public class DnsPropagationPoller : IDnsPropagationPoller
{
	private readonly DnsOptions _options;
	private readonly IDnsTxtResolver _resolver;
	private readonly ILogger<DnsPropagationPoller> _logger;
	private readonly TimeProvider _timeProvider;

	public DnsPropagationPoller(
		IOptions<ProxyOptions> options,
		IDnsTxtResolver resolver,
		ILogger<DnsPropagationPoller> logger,
		TimeProvider? timeProvider = null)
	{
		_options = options.Value.Dns;
		_resolver = resolver;
		_logger = logger;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>
	/// Polls all configured public DNS resolvers until the expected TXT value
	/// is observed on ALL resolvers, or the configured timeout is exceeded.
	/// Throws TimeoutException on timeout.
	/// </summary>
	public async Task WaitForPropagationAsync(
		string fqdn,            // e.g. _acme-challenge.example.com
		string expectedValue,   // exact TXT value to match
		CancellationToken ct)
	{
		var resolvers = _options.ResolverAddresses
			.Select(IPAddress.Parse)
			.ToList();

		if (resolvers.Count == 0)
			throw new InvalidOperationException("No DNS resolver addresses configured.");

		var timeout = TimeSpan.FromSeconds(_options.PropagationTimeoutSeconds);
		var interval = TimeSpan.FromSeconds(_options.PropagationPollIntervalSeconds);
		var startedAt = _timeProvider.GetTimestamp();

		while (true)
		{
			ct.ThrowIfCancellationRequested();

			if (await AllResolversConfirmAsync(resolvers, fqdn, expectedValue, ct))
			{
				_logger.LogInformation("TXT value for {Fqdn} observed on all {Count} resolvers", fqdn, resolvers.Count);
				return;
			}

			if (_timeProvider.GetElapsedTime(startedAt) >= timeout)
			{
				throw new TimeoutException(
					$"TXT value for '{fqdn}' did not propagate to all resolvers within {timeout.TotalSeconds:0}s.");
			}

			await Task.Delay(interval, _timeProvider, ct);
		}
	}

	private async Task<bool> AllResolversConfirmAsync(
		List<IPAddress> resolvers, string fqdn, string expectedValue, CancellationToken ct)
	{
		foreach (var resolver in resolvers)
		{
			IReadOnlyList<string> values;
			try
			{
				values = await _resolver.QueryTxtAsync(resolver, fqdn, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "TXT query for {Fqdn} on {Resolver} failed", fqdn, resolver);
				return false;
			}

			if (!values.Contains(expectedValue, StringComparer.Ordinal))
			{
				_logger.LogDebug("TXT value for {Fqdn} not yet visible on {Resolver}", fqdn, resolver);
				return false;
			}
		}
		return true;
	}
}
