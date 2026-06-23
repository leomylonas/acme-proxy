using AcmeProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace AcmeProxy.Tests.Helpers;

public static class TestDbContextFactory
{
	public static AcmeProxyDbContext Create()
	{
		var options = new DbContextOptionsBuilder<AcmeProxyDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.Options;
		return new AcmeProxyDbContext(options);
	}
}
