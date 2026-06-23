using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class DirectoryMeta
{
	[JsonPropertyName("termsOfService")] public string TermsOfService { get; set; } = string.Empty;
}
