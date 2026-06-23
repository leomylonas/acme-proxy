using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class AccountResponse
{
	[JsonPropertyName("status")] public string Status { get; set; } = "valid";
	[JsonPropertyName("contact")] public List<string> Contact { get; set; } = new();
	[JsonPropertyName("orders")] public string Orders { get; set; } = string.Empty;
}
