using AcmeProxy.Services;
using FluentAssertions;
using Xunit;

namespace AcmeProxy.Tests.Services;

public class NonceServiceTests
{
	private readonly NonceService _sut = new();

	[Fact]
	public void IssueNonce_ReturnsNonEmptyString()
	{
		_sut.IssueNonce().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public void IssueNonce_ReturnsUniqueValues()
	{
		_sut.IssueNonce().Should().NotBe(_sut.IssueNonce());
	}

	[Fact]
	public void ConsumeNonce_ReturnsTrueForIssuedNonce()
	{
		var nonce = _sut.IssueNonce();
		_sut.ConsumeNonce(nonce).Should().BeTrue();
	}

	[Fact]
	public void ConsumeNonce_ReturnsFalseForUnknownNonce()
	{
		_sut.ConsumeNonce("not-a-real-nonce").Should().BeFalse();
	}

	[Fact]
	public void ConsumeNonce_ReturnsFalseForAlreadyConsumedNonce()
	{
		var nonce = _sut.IssueNonce();
		_sut.ConsumeNonce(nonce).Should().BeTrue();
		_sut.ConsumeNonce(nonce).Should().BeFalse();
	}

	[Fact]
	public void ConsumeNonce_IsConcurrentlySafe()
	{
		var nonce = _sut.IssueNonce();
		var successes = 0;

		Parallel.For(0, 100, _ =>
		{
			if (_sut.ConsumeNonce(nonce))
				Interlocked.Increment(ref successes);
		});

		successes.Should().Be(1);
	}
}
