using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class FinalizeRequest
{
	[JsonPropertyName("csr")] public string Csr { get; set; } = string.Empty;
}
