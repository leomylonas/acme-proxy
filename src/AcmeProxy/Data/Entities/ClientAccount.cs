namespace AcmeProxy.Data.Entities;

/// <summary>
/// An ACME account registered by an internal client (certbot, cert-manager).
/// Stores only the client's public JWK — the private key never leaves the client.
/// The <see cref="Id"/> is the account identifier embedded in the JWS 'kid' URL.
/// </summary>
public class ClientAccount
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string PublicKeyJwkJson { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; }
}
