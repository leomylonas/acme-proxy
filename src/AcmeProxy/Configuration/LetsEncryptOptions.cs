namespace AcmeProxy.Configuration;

public class LetsEncryptOptions
{
	public string AccountEmail { get; set; } = string.Empty;

	/// <summary>
	/// Optional explicit ACME directory URL. When set it overrides <see cref="UseStaging"/>
	/// and is used as-is — primarily for pointing at a test ACME server (e.g. Pebble).
	/// </summary>
	public string? DirectoryUrl { get; set; }
}
