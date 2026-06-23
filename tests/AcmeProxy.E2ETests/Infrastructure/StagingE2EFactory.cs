using AcmeProxy;
using AcmeProxy.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Hosts AcmeProxy against the <b>real</b> Let's Encrypt staging directory using the real
/// HestiaCP DNS provider and public-resolver propagation poller. Configuration comes from
/// environment variables; only the database is swapped for an isolated in-memory store.
/// </summary>
public class StagingE2EFactory : WebApplicationFactory<Program>
{
	private readonly InMemoryDatabaseRoot _dbRoot = new();
	private readonly string _dbName = Guid.NewGuid().ToString();

	public static StagingSettings Settings => StagingSettings.FromEnvironment();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		var s = Settings;

		builder.UseEnvironment("Testing");
		builder.UseSetting("Proxy:InitialiseLetsEncryptOnStartup", "true");
		builder.UseSetting("Proxy:LetsEncrypt:UseStaging", "true");
		builder.UseSetting("Proxy:LetsEncrypt:AccountEmail", s.AccountEmail);
		builder.UseSetting("Proxy:AllowedDomains:0", s.Domain);
		builder.UseSetting("Proxy:HestiaCP:BaseUrl", s.HestiaBaseUrl);
		builder.UseSetting("Proxy:HestiaCP:AccessKey", s.HestiaAccessKey);
		builder.UseSetting("Proxy:HestiaCP:SecretKey", s.HestiaSecretKey);
		builder.UseSetting("Proxy:HestiaCP:Username", s.HestiaUsername);
		builder.UseSetting("Proxy:Dns:PropagationPollIntervalSeconds", "10");
		builder.UseSetting("Proxy:Dns:PropagationTimeoutSeconds", "300");
		builder.UseSetting("Proxy:Dns:ResolverAddresses:0", "8.8.8.8");
		builder.UseSetting("Proxy:Dns:ResolverAddresses:1", "1.1.1.1");

		builder.ConfigureServices(services =>
			E2EDbReplacement.UseInMemory(services, _dbName, _dbRoot));
	}

	public AcmeProxyDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<AcmeProxyDbContext>().UseInMemoryDatabase(_dbName, _dbRoot).Options);
}

public record StagingSettings(
	bool Enabled,
	string Domain,
	string AccountEmail,
	string HestiaBaseUrl,
	string HestiaAccessKey,
	string HestiaSecretKey,
	string HestiaUsername)
{
	public static StagingSettings FromEnvironment()
	{
		string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

		return new StagingSettings(
			Enabled: Env("ACMEPROXY_E2E_STAGING") == "1",
			Domain: Env("E2E_DOMAIN"),
			AccountEmail: string.IsNullOrEmpty(Env("E2E_ACCOUNT_EMAIL")) ? "e2e@example.com" : Env("E2E_ACCOUNT_EMAIL"),
			HestiaBaseUrl: Env("E2E_HESTIA_BASEURL"),
			HestiaAccessKey: Env("E2E_HESTIA_ACCESSKEY"),
			HestiaSecretKey: Env("E2E_HESTIA_SECRETKEY"),
			HestiaUsername: Env("E2E_HESTIA_USERNAME"));
	}

	public bool IsComplete =>
		Enabled &&
		!string.IsNullOrWhiteSpace(Domain) &&
		!string.IsNullOrWhiteSpace(HestiaBaseUrl) &&
		!string.IsNullOrWhiteSpace(HestiaAccessKey) &&
		!string.IsNullOrWhiteSpace(HestiaSecretKey) &&
		!string.IsNullOrWhiteSpace(HestiaUsername);
}
