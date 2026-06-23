using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>
/// Minimal ACME JWS signer (RS256) for driving AcmeProxy's ACME HTTP API as a client.
/// Uses a single RSA account key; supports both jwk (new-account) and kid signing.
/// </summary>
public class JwsSigner
{
	private readonly RSA _key = RSA.Create(2048);

	public string SignWithJwk(string nonce, object? payload, string? url = null)
		=> Sign(nonce, payload, url, kid: null);

	public string SignWithKid(string nonce, string kid, object? payload, string? url = null)
		=> Sign(nonce, payload, url, kid);

	private string Sign(string nonce, object? payload, string? url, string? kid)
	{
		var header = new Dictionary<string, object>
		{
			["alg"] = "RS256",
			["nonce"] = nonce,
		};
		if (url is not null)
			header["url"] = url;

		if (kid is null)
		{
			var p = _key.ExportParameters(false);
			header["jwk"] = new Dictionary<string, string>
			{
				["kty"] = "RSA",
				["n"] = Base64Url(p.Modulus!),
				["e"] = Base64Url(p.Exponent!),
			};
		}
		else
		{
			header["kid"] = kid;
		}

		var protectedB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
		var payloadB64 = payload is null ? "" : Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
		var signature = _key.SignData(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"),
			HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

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
