using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class Identifier
{
	[JsonPropertyName("type")] public string Type { get; set; } = "dns";
	[JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
}
