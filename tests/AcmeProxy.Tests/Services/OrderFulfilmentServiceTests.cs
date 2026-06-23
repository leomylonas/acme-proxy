using AcmeProxy.Data;
using AcmeProxy.Data.Entities;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Services;
using AcmeProxy.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AcmeProxy.Tests.Services;

public class OrderFulfilmentServiceTests
{
	private readonly Mock<ILetsEncryptClient> _le = new();
	private readonly Mock<IDnsProviderPlugin> _dns = new();
	private readonly Mock<IDnsPropagationPoller> _poller = new();

	private static readonly LeOrderContext LeOrder = new("https://le/order/1", "tok", "txt-value");

	private OrderFulfilmentService Build(AcmeProxyDbContext db) =>
		new(db, _le.Object, _dns.Object, _poller.Object, NullLogger<OrderFulfilmentService>.Instance);

	private static (AcmeProxyDbContext db, Guid challengeId) Seed(string domain = "example.com")
	{
		var db = TestDbContextFactory.Create();
		var order = new ProxyOrder
		{
			Domain = domain,
			IdentifiersJson = $"[\"{domain}\"]",
			Status = "pending",
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
		};
		var authz = new ProxyAuthorization { OrderId = order.Id, Domain = domain, Status = "pending" };
		var challenge = new ProxyChallenge { AuthorizationId = authz.Id, Token = "t", Status = "pending" };
		db.Orders.Add(order);
		db.Authorizations.Add(authz);
		db.Challenges.Add(challenge);
		db.SaveChanges();
		return (db, challenge.Id);
	}

	private void SetupHappyPath()
	{
		_le.Setup(x => x.CreateOrderAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(LeOrder);
		_dns.Setup(x => x.AddTxtRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync("rec-1");
	}

	[Fact]
	public async Task Fulfil_SetsChallengeToProcessing_ThenValid()
	{
		SetupHappyPath();
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		var challenge = await db.Challenges.FindAsync(id);
		var authz = await db.Authorizations.FirstAsync();
		var order = await db.Orders.FirstAsync();
		challenge!.Status.Should().Be("valid");
		authz.Status.Should().Be("valid");
		order.Status.Should().Be("ready");
	}

	[Fact]
	public async Task Fulfil_PersistsDnsTxtValue()
	{
		SetupHappyPath();
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		(await db.Challenges.FindAsync(id))!.TxtValue.Should().Be("txt-value");
	}

	[Fact]
	public async Task Fulfil_PersistsHestiaRecordId()
	{
		SetupHappyPath();
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		(await db.Challenges.FindAsync(id))!.HestiaDnsRecordId.Should().Be("rec-1");
	}

	[Fact]
	public async Task Fulfil_CleansDnsRecord_AfterValidation()
	{
		SetupHappyPath();
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		_dns.Verify(x => x.DeleteTxtRecordAsync("example.com", "_acme-challenge", "rec-1", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Fulfil_SetsStatusToInvalid_OnDnsPropagationTimeout()
	{
		SetupHappyPath();
		_poller.Setup(x => x.WaitForPropagationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new TimeoutException());
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		(await db.Challenges.FindAsync(id))!.Status.Should().Be("invalid");
		(await db.Orders.FirstAsync()).Status.Should().Be("invalid");
	}

	[Fact]
	public async Task Fulfil_SetsStatusToInvalid_OnLeValidationFailure()
	{
		SetupHappyPath();
		_le.Setup(x => x.WaitForChallengeValidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("invalid"));
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		(await db.Challenges.FindAsync(id))!.Status.Should().Be("invalid");
	}

	[Fact]
	public async Task Fulfil_CleansDnsRecord_EvenOnFailure()
	{
		SetupHappyPath();
		_le.Setup(x => x.WaitForChallengeValidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("invalid"));
		var (db, id) = Seed();

		await Build(db).FulfilAsync(id);

		_dns.Verify(x => x.DeleteTxtRecordAsync("example.com", "_acme-challenge", "rec-1", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
	}

	[Fact]
	public async Task Fulfil_IsIdempotent_WhenChallengeAlreadyProcessing()
	{
		var (db, id) = Seed();
		var challenge = await db.Challenges.FindAsync(id);
		challenge!.Status = "processing";
		await db.SaveChangesAsync();

		await Build(db).FulfilAsync(id);

		_le.Verify(x => x.CreateOrderAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Fulfil_SerialisesConcurrentOrdersForSameDomain()
	{
		SetupHappyPath();
		var domain = $"concurrent-{Guid.NewGuid():N}.com";

		var (db1, id1) = Seed(domain);
		var (db2, id2) = Seed(domain);

		var concurrent = 0;
		var maxConcurrent = 0;
		_poller.Setup(x => x.WaitForPropagationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Returns(async () =>
			{
				var now = Interlocked.Increment(ref concurrent);
				maxConcurrent = Math.Max(maxConcurrent, now);
				await Task.Delay(50);
				Interlocked.Decrement(ref concurrent);
			});

		await Task.WhenAll(
			Build(db1).FulfilAsync(id1),
			Build(db2).FulfilAsync(id2));

		maxConcurrent.Should().Be(1);
	}
}
