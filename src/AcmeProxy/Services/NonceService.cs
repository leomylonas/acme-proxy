using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AcmeProxy.Services;

/// <summary>
/// Issues and validates single-use replay nonces for ACME requests.
/// </summary>
public class NonceService
{
	private readonly ConcurrentDictionary<string, byte> _issued = new();

	/// <summary>Issues a new nonce and enqueues it for later validation.</summary>
	public string IssueNonce()
	{
		var bytes = RandomNumberGenerator.GetBytes(16);
		var nonce = Base64UrlEncode(bytes);
		_issued[nonce] = 0;
		return nonce;
	}

	/// <summary>
	/// Validates and consumes a nonce. Returns false if the nonce was not issued
	/// or has already been consumed.
	/// </summary>
	public bool ConsumeNonce(string nonce)
	{
		if (string.IsNullOrEmpty(nonce))
			return false;
		return _issued.TryRemove(nonce, out _);
	}

	private static string Base64UrlEncode(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
