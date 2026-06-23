using System.Text.Json.Serialization;

namespace AcmeProxy.Models.Acme;

public class DirectoryModel
{
	[JsonPropertyName("newNonce")] public string NewNonce { get; set; } = string.Empty;
	[JsonPropertyName("newAccount")] public string NewAccount { get; set; } = string.Empty;
	[JsonPropertyName("newOrder")] public string NewOrder { get; set; } = string.Empty;
	[JsonPropertyName("revokeCert")] public string RevokeCert { get; set; } = string.Empty;
	[JsonPropertyName("keyChange")] public string KeyChange { get; set; } = string.Empty;
	[JsonPropertyName("meta")] public DirectoryMeta Meta { get; set; } = new();
}
