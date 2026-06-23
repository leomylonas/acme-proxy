using AcmeProxy;
using AcmeProxy.Data;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AcmeProxy.Tests.Controllers;

public class AcmeWebApplicationFactory : WebApplicationFactory<Program>
{
	private readonly InMemoryDatabaseRoot _dbRoot = new();
	private readonly string _dbName = Guid.NewGuid().ToString();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");
		builder.UseSetting("Proxy:InitialiseLetsEncryptOnStartup", "false");
		builder.UseSetting("Proxy:AllowedDomains:0", "example.com");
		builder.UseSetting("ConnectionStrings:Default", "DataSource=:memory:");

		builder.ConfigureServices(services =>
		{
			// Strip every registration the production AddDbContext added (options,
			// configuration, the context itself) so only the in-memory provider remains.
			foreach (var d in services.Where(s =>
						 s.ServiceType == typeof(AcmeProxyDbContext) ||
						 (s.ServiceType.FullName?.Contains("DbContextOptions") ?? false) ||
						 (s.ServiceType.FullName?.Contains(nameof(AcmeProxyDbContext)) ?? false))
					 .ToList())
			{
				services.Remove(d);
			}
			services.AddDbContext<AcmeProxyDbContext>(o => o.UseInMemoryDatabase(_dbName, _dbRoot));

			Replace<ILetsEncryptClient>(services, new StubLetsEncryptClient());
			Replace<IDnsProviderPlugin>(services, new StubDnsProvider());
			Replace<IDnsPropagationPoller>(services, new StubPoller());
		});
	}

	public AcmeProxyDbContext CreateDbContext()
	{
		var options = new DbContextOptionsBuilder<AcmeProxyDbContext>()
			.UseInMemoryDatabase(_dbName, _dbRoot)
			.Options;
		return new AcmeProxyDbContext(options);
	}

	private static void RemoveAll<T>(IServiceCollection services)
	{
		foreach (var d in services.Where(s => s.ServiceType == typeof(T)).ToList())
			services.Remove(d);
	}

	private static void Replace<T>(IServiceCollection services, T instance) where T : class
	{
		RemoveAll<T>(services);
		services.AddSingleton(instance);
	}

	private sealed class StubLetsEncryptClient : ILetsEncryptClient
	{
		public Task InitialiseAsync(CancellationToken ct) => Task.CompletedTask;
		public Task<LeOrderContext> CreateOrderAsync(IEnumerable<string> identifiers, CancellationToken ct)
			=> Task.FromResult(new LeOrderContext("https://le/order/1", "le-token", "le-txt-value"));
		public Task NotifyChallengeReadyAsync(string leOrderUrl, CancellationToken ct) => Task.CompletedTask;
		public Task WaitForChallengeValidationAsync(string leOrderUrl, CancellationToken ct) => Task.CompletedTask;
		public Task<string> FinalizeOrderAsync(string leOrderUrl, byte[] csrDer, CancellationToken ct)
			=> Task.FromResult("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
	}

	private sealed class StubDnsProvider : IDnsProviderPlugin
	{
		public Task<string> AddTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct)
			=> Task.FromResult("rec-1");
		public Task DeleteTxtRecordAsync(string domain, string recordName, string recordId, CancellationToken ct)
			=> Task.CompletedTask;
	}

	private sealed class StubPoller : IDnsPropagationPoller
	{
		public Task WaitForPropagationAsync(string fqdn, string expectedValue, CancellationToken ct) => Task.CompletedTask;
	}
}
