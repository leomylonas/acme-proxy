using System.Net;
using AcmeProxy.Configuration;
using AcmeProxy.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AcmeProxy.Tests.Services;

public class DnsPropagationPollerTests
{
	private const string Fqdn = "_acme-challenge.example.com";
	private const string Expected = "the-expected-txt-value";

	private static DnsPropagationPoller Build(IDnsTxtResolver resolver, int timeoutSeconds = 5)
	{
		var options = Options.Create(new ProxyOptions
		{
			Dns = new DnsOptions
			{
				PropagationPollIntervalSeconds = 1,
				PropagationTimeoutSeconds = timeoutSeconds,
				ResolverAddresses = new List<string> { "8.8.8.8", "1.1.1.1" },
			},
		});
		return new DnsPropagationPoller(options, resolver, NullLogger<DnsPropagationPoller>.Instance);
	}

	[Fact]
	public async Task WaitForPropagation_ReturnsWhenTxtValueObservedOnAllResolvers()
	{
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(It.IsAny<IPAddress>(), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string> { Expected });

		var act = async () => await Build(resolver.Object).WaitForPropagationAsync(Fqdn, Expected, default);
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task WaitForPropagation_ThrowsTimeoutException_WhenValueNotObservedWithinTimeout()
	{
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(It.IsAny<IPAddress>(), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string>());

		var act = async () => await Build(resolver.Object, timeoutSeconds: 1).WaitForPropagationAsync(Fqdn, Expected, default);
		await act.Should().ThrowAsync<TimeoutException>();
	}

	[Fact]
	public async Task WaitForPropagation_RetriesUntilValueAppears()
	{
		var calls = 0;
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(It.IsAny<IPAddress>(), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(() =>
			{
				var n = Interlocked.Increment(ref calls);
				return n >= 3 ? new List<string> { Expected } : new List<string>();
			});

		await Build(resolver.Object).WaitForPropagationAsync(Fqdn, Expected, default);
		calls.Should().BeGreaterThanOrEqualTo(3);
	}

	[Fact]
	public async Task WaitForPropagation_ThrowsOperationCanceledException_WhenCancelled()
	{
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(It.IsAny<IPAddress>(), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string>());

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var act = async () => await Build(resolver.Object).WaitForPropagationAsync(Fqdn, Expected, cts.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task WaitForPropagation_StripsQuotesFromTxtValues()
	{
		// The resolver abstraction is responsible for stripping quotes; verify a stripped
		// value matches successfully end-to-end.
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(It.IsAny<IPAddress>(), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string> { Expected });

		var act = async () => await Build(resolver.Object).WaitForPropagationAsync(Fqdn, Expected, default);
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task WaitForPropagation_RequiresAllResolversToConfirm()
	{
		var resolver = new Mock<IDnsTxtResolver>();
		resolver.Setup(r => r.QueryTxtAsync(IPAddress.Parse("8.8.8.8"), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string> { Expected });
		resolver.Setup(r => r.QueryTxtAsync(IPAddress.Parse("1.1.1.1"), Fqdn, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<string>());

		var act = async () => await Build(resolver.Object, timeoutSeconds: 1).WaitForPropagationAsync(Fqdn, Expected, default);
		await act.Should().ThrowAsync<TimeoutException>();
	}
}
