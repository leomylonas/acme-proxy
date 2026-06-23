using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class OrderResponse
{
	[JsonPropertyName("status")] public string Status { get; set; } = "pending";
	[JsonPropertyName("identifiers")] public List<Identifier> Identifiers { get; set; } = new();
	[JsonPropertyName("authorizations")] public List<string> Authorizations { get; set; } = new();
	[JsonPropertyName("finalize")] public string Finalize { get; set; } = string.Empty;
	[JsonPropertyName("certificate")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Certificate { get; set; }
}
