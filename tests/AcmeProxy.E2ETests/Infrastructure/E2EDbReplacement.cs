using AcmeProxy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AcmeProxy.E2ETests.Infrastructure;

internal static class E2EDbReplacement
{
	/// <summary>
	/// Replaces the production SQLite DbContext registration with an isolated in-memory one.
	/// </summary>
	public static void UseInMemory(IServiceCollection services, string name, InMemoryDatabaseRoot root)
	{
		foreach (var d in services.Where(s =>
					 s.ServiceType == typeof(AcmeProxyDbContext) ||
					 (s.ServiceType.FullName?.Contains("DbContextOptions") ?? false) ||
					 (s.ServiceType.FullName?.Contains(nameof(AcmeProxyDbContext)) ?? false))
				 .ToList())
		{
			services.Remove(d);
		}
		services.AddDbContext<AcmeProxyDbContext>(o => o.UseInMemoryDatabase(name, root));
	}
}
