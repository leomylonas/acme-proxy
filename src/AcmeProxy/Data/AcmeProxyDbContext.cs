using AcmeProxy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AcmeProxy.Data;

public class AcmeProxyDbContext : DbContext
{
	public DbSet<LeAccount> LeAccounts => Set<LeAccount>();
	public DbSet<ClientAccount> ClientAccounts => Set<ClientAccount>();
	public DbSet<ProxyOrder> Orders => Set<ProxyOrder>();
	public DbSet<ProxyAuthorization> Authorizations => Set<ProxyAuthorization>();
	public DbSet<ProxyChallenge> Challenges => Set<ProxyChallenge>();
	public DbSet<ProxyCertificate> Certificates => Set<ProxyCertificate>();

	public AcmeProxyDbContext(DbContextOptions<AcmeProxyDbContext> options) : base(options) { }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<ProxyOrder>()
			.HasOne(o => o.Authorization)
			.WithOne(a => a.Order)
			.HasForeignKey<ProxyAuthorization>(a => a.OrderId);

		modelBuilder.Entity<ProxyOrder>()
			.HasOne(o => o.Certificate)
			.WithOne(c => c.Order)
			.HasForeignKey<ProxyCertificate>(c => c.OrderId);

		modelBuilder.Entity<ProxyAuthorization>()
			.HasOne(a => a.Challenge)
			.WithOne(c => c.Authorization)
			.HasForeignKey<ProxyChallenge>(c => c.AuthorizationId);
	}
}
