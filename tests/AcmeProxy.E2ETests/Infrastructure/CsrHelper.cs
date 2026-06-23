using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AcmeProxy.E2ETests.Infrastructure;

/// <summary>Generates a PKCS#10 CSR (DER) for a single DNS name, as an ACME client would.</summary>
public static class CsrHelper
{
	public static byte[] CreateCsrDer(string commonName)
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		var san = new SubjectAlternativeNameBuilder();
		san.AddDnsName(commonName);
		request.CertificateExtensions.Add(san.Build());

		return request.CreateSigningRequest();
	}

	public static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
