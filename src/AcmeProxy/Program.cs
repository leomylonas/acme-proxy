using AcmeProxy.Configuration;
using AcmeProxy.Data;
using AcmeProxy.HestiaCP;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace AcmeProxy;

public class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		// Optional local, git-ignored overrides (e.g. HestiaCP secrets for local runs).
		// Layered last so it wins over appsettings.json and appsettings.{Environment}.json.
		// Skipped under the "Testing" environment so automated tests stay isolated from
		// developer-local configuration.
		if (!builder.Environment.IsEnvironment("Testing"))
		{
			builder.Configuration.AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true);
		}

		builder.Logging.ClearProviders();
		builder.Host.UseSerilog((context, services, loggerConfiguration) =>
			loggerConfiguration.ReadFrom.Configuration(context.Configuration));

		ConfigureServices(builder);

		var app = builder.Build();

		await InitialiseAsync(app);

		app.MapControllers();

		await app.RunAsync();
	}

	private static void ConfigureServices(WebApplicationBuilder builder)
	{
		var services = builder.Services;

		services.AddDbContext<AcmeProxyDbContext>(options =>
			options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

		services.AddOptions<ProxyOptions>()
			.Bind(builder.Configuration.GetSection(ProxyOptions.Section));

		// Named HTTP clients with a logging handler so the HestiaCP and Let's Encrypt
		// conversations are visible at Debug level when diagnosing issues.
		services.AddHttpClient(HestiaCPClient.HttpClientName)
			.AddHttpMessageHandler(sp => new LoggingHttpMessageHandler(
				sp.GetRequiredService<ILoggerFactory>().CreateLogger("HttpClient.HestiaCP"), "HestiaCP"));

		services.AddHttpClient(LetsEncryptClient.HttpClientName)
			.AddHttpMessageHandler(sp => new LoggingHttpMessageHandler(
				sp.GetRequiredService<ILoggerFactory>().CreateLogger("HttpClient.LetsEncrypt"), "LetsEncrypt"));

		services.AddSingleton<NonceService>();
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<IDnsTxtResolver, DnsClientTxtResolver>();
		services.AddSingleton<IDnsProviderPlugin, HestiaCPClient>();
		services.AddScoped<IDnsPropagationPoller, DnsPropagationPoller>();
		// Singleton: holds the certes AcmeContext established once by InitialiseAsync.
		services.AddSingleton<ILetsEncryptClient, LetsEncryptClient>();
		services.AddScoped<OrderFulfilmentService>();

		services.AddControllers();
	}

	private static async Task InitialiseAsync(WebApplication app)
	{
		using var scope = app.Services.CreateScope();
		var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

		var db = scope.ServiceProvider.GetRequiredService<AcmeProxyDbContext>();
		if (db.Database.IsRelational())
		{
			logger.LogInformation("Applying database migrations");
			await db.Database.MigrateAsync();
		}

		if (app.Configuration.GetValue("Proxy:InitialiseLetsEncryptOnStartup", true))
		{
			var le = scope.ServiceProvider.GetRequiredService<ILetsEncryptClient>();
			await le.InitialiseAsync(CancellationToken.None);
		}
		else
		{
			logger.LogInformation("Skipping Let's Encrypt initialisation (Proxy:InitialiseLetsEncryptOnStartup=false)");
		}
	}
}
