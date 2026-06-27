using AcmeProxy.Configuration;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AcmeProxy.Tests.LetsEncrypt;

// NOTE: The certes AcmeContext talks to a live ACME directory, so the order/challenge/finalize
// flows are exercised by the end-to-end smoke test against LE staging rather than unit tests.
// These tests cover the behaviour that is deterministic offline.
public class LetsEncryptClientTests
{
	private static LetsEncryptClient Build()
	{
		var options = Options.Create(new ProxyOptions
		{
			LetsEncrypt = new LetsEncryptOptions { AccountEmail = "admin@example.com" },
		});
		var httpFactory = new Mock<IHttpClientFactory>();
		httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
		var scopeFactory = new Mock<IServiceScopeFactory>();
		return new LetsEncryptClient(scopeFactory.Object, httpFactory.Object, options, isStaging: true, NullLogger<LetsEncryptClient>.Instance);
	}

	[Fact]
	public async Task CreateOrder_ThrowsWhenNotInitialised()
	{
		var act = async () => await Build().CreateOrderAsync(new[] { "example.com" }, default);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task Finalize_ThrowsWhenNotInitialised()
	{
		var act = async () => await Build().FinalizeOrderAsync("https://le/order/1", new byte[] { 1, 2, 3 }, default);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
