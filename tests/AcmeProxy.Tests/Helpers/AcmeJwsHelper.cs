using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AcmeProxy.Tests.Helpers;

/// <summary>
/// Generates valid flattened JWS-signed ACME request bodies using an in-memory RSA key pair.
/// </summary>
public static class AcmeJwsHelper
{
	private static readonly RSA SharedKey = RSA.Create(2048);

	public static string CreateSignedBody(string nonce, object? payload, string? url = null)
		=> CreateSignedBody(SharedKey, nonce, payload, url);

	public static string CreateSignedBody(RSA key, string nonce, object? payload, string? url = null)
	{
		var parameters = key.ExportParameters(false);
		var jwk = new Dictionary<string, string>
		{
			["kty"] = "RSA",
			["n"] = Base64Url(parameters.Modulus!),
			["e"] = Base64Url(parameters.Exponent!),
		};

		var header = new Dictionary<string, object>
		{
			["alg"] = "RS256",
			["nonce"] = nonce,
			["jwk"] = jwk,
		};
		if (url is not null)
			header["url"] = url;

		var protectedB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
		var payloadB64 = payload is null
			? ""
			: Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));

		var signingInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
		var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		return JsonSerializer.Serialize(new Dictionary<string, string>
		{
			["protected"] = protectedB64,
			["payload"] = payloadB64,
			["signature"] = Base64Url(signature),
		});
	}

	/// <summary>The RSA key whose public JWK <see cref="CreateSignedBody(string, object?, string?)"/> embeds.</summary>
	public static RSA AccountKey => SharedKey;

	/// <summary>
	/// Creates a kid-signed body (no embedded JWK) using the given key.
	/// </summary>
	public static string CreateKidSignedBody(RSA key, string nonce, string kid, object? payload, string? url = null)
	{
		var header = new Dictionary<string, object>
		{
			["alg"] = "RS256",
			["nonce"] = nonce,
			["kid"] = kid,
		};
		if (url is not null)
			header["url"] = url;

		var protectedB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
		var payloadB64 = payload is null ? "" : Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));

		var signingInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
		var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		return JsonSerializer.Serialize(new Dictionary<string, string>
		{
			["protected"] = protectedB64,
			["payload"] = payloadB64,
			["signature"] = Base64Url(signature),
		});
	}

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
