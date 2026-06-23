using System.Diagnostics;
using Xunit;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Collection fixture that brings up the Pebble + challtestsrv Docker stack for the
/// duration of the e2e tests. Only runs when ACMEPROXY_E2E=1 so that ordinary
/// `dotnet test` runs stay hermetic and fast.
/// </summary>
public class PebbleFixture : IAsyncLifetime
{
	public const string DirectoryUrl = "https://localhost:14000/dir";
	public const string ManagementUrl = "http://localhost:8055";
	public const int DnsPort = 8053;

	private static readonly string ComposeFile =
		Path.Combine(AppContext.BaseDirectory, "pebble", "docker-compose.pebble.yml");

	public bool Enabled { get; } =
		Environment.GetEnvironmentVariable("ACMEPROXY_E2E") == "1";

	public async Task InitializeAsync()
	{
		if (!Enabled)
			return;

		Compose("up", "-d");
		await WaitForDirectoryAsync(TimeSpan.FromSeconds(90));
	}

	public Task DisposeAsync()
	{
		if (Enabled)
			Compose("down", "-v");
		return Task.CompletedTask;
	}

	private static void Compose(params string[] args)
	{
		var psi = new ProcessStartInfo("docker")
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		psi.ArgumentList.Add("compose");
		psi.ArgumentList.Add("-f");
		psi.ArgumentList.Add(ComposeFile);
		foreach (var a in args)
			psi.ArgumentList.Add(a);

		using var process = Process.Start(psi)!;
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			var stderr = process.StandardError.ReadToEnd();
			throw new InvalidOperationException($"docker compose {string.Join(' ', args)} failed:\n{stderr}");
		}
	}

	private static async Task WaitForDirectoryAsync(TimeSpan timeout)
	{
		using var http = new HttpClient(new HttpClientHandler
		{
			ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
		});
		// Pebble rejects requests without a User-Agent header.
		http.DefaultRequestHeaders.UserAgent.ParseAdd("AcmeProxy-E2E/1.0");

		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				var response = await http.GetAsync(DirectoryUrl);
				if (response.IsSuccessStatusCode)
					return;
			}
			catch
			{
				// not ready yet
			}
			await Task.Delay(TimeSpan.FromSeconds(1));
		}

		throw new TimeoutException($"Pebble directory at {DirectoryUrl} did not become ready within {timeout.TotalSeconds:0}s.");
	}
}

[CollectionDefinition(Name)]
public class PebbleCollection : ICollectionFixture<PebbleFixture>
{
	public const string Name = "pebble";
}
