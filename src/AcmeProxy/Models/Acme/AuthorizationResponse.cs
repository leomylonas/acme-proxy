using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class AuthorizationResponse
{
	[JsonPropertyName("status")] public string Status { get; set; } = "pending";
	[JsonPropertyName("identifier")] public Identifier Identifier { get; set; } = new();
	[JsonPropertyName("challenges")] public List<ChallengeResponse> Challenges { get; set; } = new();
}
