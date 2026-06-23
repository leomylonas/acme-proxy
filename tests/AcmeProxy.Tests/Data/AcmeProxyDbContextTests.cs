using AcmeProxy.Data.Entities;
using AcmeProxy.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AcmeProxy.Tests.Data;

public class AcmeProxyDbContextTests
{
	[Fact]
	public async Task CanInsertAndRetrieveLeAccount()
	{
		await using var db = TestDbContextFactory.Create();
		db.LeAccounts.Add(new LeAccount { Email = "a@b.com", AccountKeyPem = "pem", AccountUri = "uri", CreatedAt = DateTime.UtcNow });
		await db.SaveChangesAsync();

		var loaded = await db.LeAccounts.SingleAsync();
		loaded.Email.Should().Be("a@b.com");
	}

	[Fact]
	public async Task CanInsertAndRetrieveOrderWithAuthorizationAndChallenge()
	{
		await using var db = TestDbContextFactory.Create();
		var order = new ProxyOrder { Domain = "example.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		var authz = new ProxyAuthorization { OrderId = order.Id, Domain = "example.com" };
		var challenge = new ProxyChallenge { AuthorizationId = authz.Id, Token = "tok" };
		db.AddRange(order, authz, challenge);
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();

		var loaded = await db.Orders
			.Include(o => o.Authorization)
			.ThenInclude(a => a!.Challenge)
			.SingleAsync();

		loaded.Authorization!.Domain.Should().Be("example.com");
		loaded.Authorization.Challenge!.Token.Should().Be("tok");
	}

	[Fact]
	public async Task CanInsertAndRetrieveCertificate()
	{
		await using var db = TestDbContextFactory.Create();
		var order = new ProxyOrder { Domain = "example.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		var cert = new ProxyCertificate { OrderId = order.Id, CertificateChainPem = "PEM", IssuedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(90) };
		db.AddRange(order, cert);
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();

		var loaded = await db.Orders.Include(o => o.Certificate).SingleAsync();
		loaded.Certificate!.CertificateChainPem.Should().Be("PEM");
	}

	[Fact]
	public async Task OrderStatus_UpdatesCorrectly()
	{
		await using var db = TestDbContextFactory.Create();
		var order = new ProxyOrder { Domain = "example.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Orders.Add(order);
		await db.SaveChangesAsync();

		order.Status = "ready";
		await db.SaveChangesAsync();

		(await db.Orders.SingleAsync()).Status.Should().Be("ready");
	}

	[Fact]
	public async Task ChallengeHestiaRecordId_IsNullable()
	{
		await using var db = TestDbContextFactory.Create();
		var challenge = new ProxyChallenge { AuthorizationId = Guid.NewGuid(), Token = "t", HestiaDnsRecordId = null };
		db.Challenges.Add(challenge);

		var act = async () => await db.SaveChangesAsync();
		await act.Should().NotThrowAsync();
	}
}
