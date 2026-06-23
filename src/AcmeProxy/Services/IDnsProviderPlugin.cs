namespace AcmeProxy.Services;

/// <summary>
/// Abstraction for DNS provider challenge fulfilment.
/// Returns a provider-specific record ID from AddTxtRecordAsync for use in DeleteTxtRecordAsync.
/// </summary>
public interface IDnsProviderPlugin
{
	Task<string> AddTxtRecordAsync(
		string domain,
		string recordName,
		string value,
		CancellationToken ct);

	Task DeleteTxtRecordAsync(
		string domain,
		string recordName,
		string recordId,
		CancellationToken ct);
}
