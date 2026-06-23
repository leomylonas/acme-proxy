using AcmeProxy;
using AcmeProxy.Data;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Hosts AcmeProxy wired to point its outbound ACME client at Pebble and its DNS edges at
/// pebble-challtestsrv, exercising the real certes order/validate/finalize flow.
/// </summary>
public class PebbleE2EFactory : WebApplicationFactory<Program>
{
	private readonly InMemoryDatabaseRoot _dbRoot = new();
	private readonly string _dbName = Guid.NewGuid().ToString();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");
		builder.UseSetting("Proxy:InitialiseLetsEncryptOnStartup", "true");
		builder.UseSetting("Proxy:LetsEncrypt:DirectoryUrl", PebbleFixture.DirectoryUrl);
		builder.UseSetting("Proxy:LetsEncrypt:AccountEmail", "e2e@example.com");
		builder.UseSetting("Proxy:AllowedDomains:0", "example.com");
		builder.UseSetting("Proxy:Dns:PropagationPollIntervalSeconds", "1");
		builder.UseSetting("Proxy:Dns:PropagationTimeoutSeconds", "60");
		builder.UseSetting("Proxy:Dns:ResolverAddresses:0", "127.0.0.1");

		if (Environment.GetEnvironmentVariable("ACMEPROXY_E2E_LOG") == "1")
		{
			builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddSimpleConsole());
		}

		builder.ConfigureServices(services =>
		{
			E2EDbReplacement.UseInMemory(services, _dbName, _dbRoot);

			// Trust Pebble's self-signed ACME certificate.
			services.AddHttpClient(LetsEncryptClient.HttpClientName)
				.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
				{
					ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
				});

			// Route DNS challenge fulfilment and propagation checks through challtestsrv.
			services.AddSingleton(new ChallTestSrvClient(PebbleFixture.ManagementUrl));
			Replace<IDnsProviderPlugin>(services, sp => new ChallTestSrvDnsProvider(sp.GetRequiredService<ChallTestSrvClient>()));
			Replace<IDnsTxtResolver>(services, _ => new ChallTestSrvResolver(PebbleFixture.DnsPort));
		});
	}

	public AcmeProxyDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<AcmeProxyDbContext>().UseInMemoryDatabase(_dbName, _dbRoot).Options);

	private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
	{
		foreach (var d in services.Where(s => s.ServiceType == typeof(T)).ToList())
			services.Remove(d);
		services.AddSingleton<T>(factory);
	}
}
