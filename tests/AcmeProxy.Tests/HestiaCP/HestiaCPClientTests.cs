using System.Net;
using AcmeProxy.Configuration;
using AcmeProxy.HestiaCP;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AcmeProxy.Tests.HestiaCP;

public class HestiaCPClientTests
{
	private const string Username = "admin";
	private const string Domain = "example.com";
	private const string Record = "_acme-challenge";
	private const string Value = "txt-value-123";

	private sealed class RecordingHandler : HttpMessageHandler
	{
		private readonly Func<string, HttpResponseMessage> _responder;
		public List<string> Bodies { get; } = new();

		public RecordingHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
			Bodies.Add(body);
			return _responder(body);
		}
	}

	private static (HestiaCPClient client, RecordingHandler handler) Build(Func<string, HttpResponseMessage> responder)
	{
		var handler = new RecordingHandler(responder);
		var httpClient = new HttpClient(handler);
		var factory = new Mock<IHttpClientFactory>();
		factory.Setup(f => f.CreateClient(HestiaCPClient.HttpClientName)).Returns(httpClient);

		var options = Options.Create(new ProxyOptions
		{
			HestiaCP = new HestiaCPOptions
			{
				BaseUrl = "https://panel.example.com:8083",
				AccessKey = "ak",
				SecretKey = "sk",
				Username = Username,
			},
		});

		var client = new HestiaCPClient(factory.Object, options, NullLogger<HestiaCPClient>.Instance);
		return (client, handler);
	}

	private static HttpResponseMessage Ok(string content) =>
		new(HttpStatusCode.OK) { Content = new StringContent(content) };

	private static string ListJson() =>
		$$"""
		{ "42": { "RECORD": "{{Record}}", "TYPE": "TXT", "VALUE": "{{Value}}", "TTL": "60" } }
		""";

	[Fact]
	public async Task AddTxtRecord_PostsCorrectCommand()
	{
		var (client, handler) = Build(body => body.Contains("v-add-dns-record") ? Ok("0") : Ok(ListJson()));

		await client.AddTxtRecordAsync(Domain, Record, Value, default);

		handler.Bodies[0].Should().Contain("cmd=v-add-dns-record");
		handler.Bodies[0].Should().Contain($"arg1={Username}");
		handler.Bodies[0].Should().Contain($"arg2={Domain}");
		handler.Bodies[0].Should().Contain($"arg3={Record}");
		handler.Bodies[0].Should().Contain("arg4=TXT");
	}

	[Fact]
	public async Task AddTxtRecord_ReturnsRecordId()
	{
		var (client, _) = Build(body => body.Contains("v-add-dns-record") ? Ok("0") : Ok(ListJson()));

		var id = await client.AddTxtRecordAsync(Domain, Record, Value, default);

		id.Should().Be("42");
	}

	[Fact]
	public async Task AddTxtRecord_ThrowsOnNonZeroResponse()
	{
		var (client, _) = Build(_ => Ok("4"));

		var act = async () => await client.AddTxtRecordAsync(Domain, Record, Value, default);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task DeleteTxtRecord_PostsCorrectCommand()
	{
		var (client, handler) = Build(_ => Ok("0"));

		await client.DeleteTxtRecordAsync(Domain, Record, "42", default);

		handler.Bodies[0].Should().Contain("cmd=v-delete-dns-record");
		handler.Bodies[0].Should().Contain($"arg1={Username}");
		handler.Bodies[0].Should().Contain($"arg2={Domain}");
		handler.Bodies[0].Should().Contain("arg3=42");
	}

	[Fact]
	public async Task DeleteTxtRecord_DoesNotThrowOnFailure()
	{
		var (client, _) = Build(_ => Ok("3"));

		var act = async () => await client.DeleteTxtRecordAsync(Domain, Record, "42", default);

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task AddTxtRecord_UsesLowTtl()
	{
		var (client, handler) = Build(body => body.Contains("v-add-dns-record") ? Ok("0") : Ok(ListJson()));

		await client.AddTxtRecordAsync(Domain, Record, Value, default);

		handler.Bodies[0].Should().Contain("arg6=60");
	}
}
