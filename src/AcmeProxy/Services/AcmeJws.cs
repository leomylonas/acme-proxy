using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AcmeProxy.Services;

/// <summary>
/// Parses and verifies flattened JWS request bodies as used by RFC 8555 ACME.
/// Supports RS256 (RSA) and ES256 (P-256 ECDSA) signatures, verified against either
/// an embedded JWK (new-account) or a stored account key resolved from the 'kid'.
/// </summary>
public static class AcmeJws
{
	public sealed record JwsRequest(
		JsonElement ProtectedHeader,
		string PayloadJson,
		string? Nonce,
		string? Url,
		string? Kid,
		string? JwkJson);

	public enum JwsError
	{
		Malformed,
		BadSignature,
		UnknownAccount,
	}

	public sealed class JwsException : Exception
	{
		public JwsError Kind { get; }
		public JwsException(JwsError kind, string message) : base(message) => Kind = kind;
	}

	/// <summary>
	/// Resolves a stored client account public JWK (as raw JSON) for a given 'kid' header
	/// value, or returns null if no such account exists.
	/// </summary>
	public delegate string? KidKeyResolver(string kid);

	/// <summary>
	/// Parses the flattened JWS body and verifies its signature. For requests carrying an
	/// embedded 'jwk' the signature is verified directly; for 'kid' requests the stored
	/// account key is resolved via <paramref name="kidResolver"/> and used for verification.
	/// Throws <see cref="JwsException"/> on malformed input, an unknown account, or a bad signature.
	/// </summary>
	public static JwsRequest ParseAndVerify(string body, KidKeyResolver? kidResolver = null)
	{
		JsonElement root;
		try
		{
			using var doc = JsonDocument.Parse(body);
			root = doc.RootElement.Clone();
		}
		catch (JsonException ex)
		{
			throw new JwsException(JwsError.Malformed, $"Malformed JWS body: {ex.Message}");
		}

		if (!root.TryGetProperty("protected", out var protectedEl) ||
			!root.TryGetProperty("signature", out var signatureEl))
		{
			throw new JwsException(JwsError.Malformed, "JWS missing 'protected' or 'signature'.");
		}

		var protectedB64 = protectedEl.GetString() ?? throw new JwsException(JwsError.Malformed, "Empty 'protected'.");
		var payloadB64 = root.TryGetProperty("payload", out var payloadEl) ? payloadEl.GetString() ?? "" : "";
		var signatureB64 = signatureEl.GetString() ?? throw new JwsException(JwsError.Malformed, "Empty 'signature'.");

		JsonElement header;
		try
		{
			var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(protectedB64));
			using var headerDoc = JsonDocument.Parse(headerJson);
			header = headerDoc.RootElement.Clone();
		}
		catch (Exception ex)
		{
			throw new JwsException(JwsError.Malformed, $"Malformed protected header: {ex.Message}");
		}

		var nonce = GetString(header, "nonce");
		var url = GetString(header, "url");
		var alg = GetString(header, "alg") ?? throw new JwsException(JwsError.Malformed, "Missing 'alg'.");
		var kid = GetString(header, "kid");

		var signingInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
		var signature = Base64UrlDecode(signatureB64);

		string? jwkJson = null;
		if (header.TryGetProperty("jwk", out var jwk))
		{
			jwkJson = jwk.GetRawText();
			VerifyWithJwk(alg, jwk, signingInput, signature);
		}
		else if (kid is not null)
		{
			var resolved = kidResolver?.Invoke(kid);
			if (resolved is null)
				throw new JwsException(JwsError.UnknownAccount, $"No registered account for kid '{kid}'.");

			jwkJson = resolved;
			using var resolvedDoc = JsonDocument.Parse(resolved);
			VerifyWithJwk(alg, resolvedDoc.RootElement, signingInput, signature);
		}
		else
		{
			throw new JwsException(JwsError.Malformed, "JWS protected header has neither 'jwk' nor 'kid'.");
		}

		var payloadJson = payloadB64.Length == 0 ? "" : Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
		return new JwsRequest(header, payloadJson, nonce, url, kid, jwkJson);
	}

	private static void VerifyWithJwk(string alg, JsonElement jwk, byte[] signingInput, byte[] signature)
	{
		var kty = GetString(jwk, "kty");
		switch (kty)
		{
			case "RSA" when alg == "RS256":
			{
				using var rsa = RSA.Create(new RSAParameters
				{
					Modulus = Base64UrlDecode(GetRequired(jwk, "n")),
					Exponent = Base64UrlDecode(GetRequired(jwk, "e")),
				});
				if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
					throw new JwsException(JwsError.BadSignature, "RSA signature verification failed.");
				break;
			}
			case "EC" when alg == "ES256":
			{
				using var ecdsa = ECDsa.Create(new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					Q = new ECPoint
					{
						X = Base64UrlDecode(GetRequired(jwk, "x")),
						Y = Base64UrlDecode(GetRequired(jwk, "y")),
					},
				});
				if (!ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256))
					throw new JwsException(JwsError.BadSignature, "ECDSA signature verification failed.");
				break;
			}
			default:
				throw new JwsException(JwsError.Malformed, $"Unsupported JWK kty '{kty}' / alg '{alg}'.");
		}
	}

	private static string? GetString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string GetRequired(JsonElement element, string name) =>
		GetString(element, name) ?? throw new JwsException(JwsError.Malformed, $"JWK missing '{name}'.");

	public static byte[] Base64UrlDecode(string input)
	{
		var s = input.Replace('-', '+').Replace('_', '/');
		switch (s.Length % 4)
		{
			case 2: s += "=="; break;
			case 3: s += "="; break;
		}
		return Convert.FromBase64String(s);
	}

	public static string Base64UrlEncode(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
