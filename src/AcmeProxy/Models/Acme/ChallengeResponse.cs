using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class ChallengeResponse
{
	[JsonPropertyName("type")] public string Type { get; set; } = "dns-01";
	[JsonPropertyName("status")] public string Status { get; set; } = "pending";
	[JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
	[JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
}
