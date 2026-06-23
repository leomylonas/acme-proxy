using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

/// <summary>RFC 7807 problem document used for ACME error responses.</summary>
public class ProblemDocument
{
	[JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
	[JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
}
