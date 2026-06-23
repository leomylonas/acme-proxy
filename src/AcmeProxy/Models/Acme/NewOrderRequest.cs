using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class NewOrderRequest
{
	[JsonPropertyName("identifiers")] public List<Identifier> Identifiers { get; set; } = new();
}
