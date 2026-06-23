using System.Text.Json;
using AcmeProxy.Configuration;
using AcmeProxy.Services;
using Microsoft.Extensions.Options;

namespace AcmeProxy.HestiaCP;

/// <summary>
/// DNS provider plugin backed by the HestiaCP Access Key API.
/// </summary>
public class HestiaCPClient : IDnsProviderPlugin
{
	public const string HttpClientName = "hestiacp";

	private readonly HttpClient _http;
	private readonly HestiaCPOptions _options;
	private readonly ILogger<HestiaCPClient> _logger;

	public HestiaCPClient(
		IHttpClientFactory httpClientFactory,
		IOptions<ProxyOptions> options,
		ILogger<HestiaCPClient> logger)
	{
		_http = httpClientFactory.CreateClient(HttpClientName);
		_options = options.Value.HestiaCP;
		_logger = logger;
	}

	public async Task<string> AddTxtRecordAsync(
		string domain,
		string recordName,
		string value,
		CancellationToken ct)
	{
		var addBody = BuildCommand("v-add-dns-record", new[]
		{
			_options.Username,
			domain,
			recordName,
			"TXT",
			value,
			"60", // low TTL for ACME
		}, returnCode: true);

		var addResponse = await PostAsync(addBody, ct);
		AssertSuccess(addResponse, "v-add-dns-record");

		// Retrieve the record ID via v-list-dns-records (JSON output, no return code).
		var listBody = BuildCommand("v-list-dns-records", new[]
		{
			_options.Username,
			domain,
			"json",
		}, returnCode: false);
		var listResponse = await PostAsync(listBody, ct);

		var recordId = FindRecordId(listResponse, recordName, value);
		if (recordId is null)
		{
			throw new InvalidOperationException(
				$"Added TXT record '{recordName}' for '{domain}' but could not locate it in v-list-dns-records response.");
		}

		_logger.LogInformation("Added TXT record {Record} for {Domain} with id {Id}", recordName, domain, recordId);
		return recordId;
	}

	public async Task DeleteTxtRecordAsync(
		string domain,
		string recordName,
		string recordId,
		CancellationToken ct)
	{
		try
		{
			var body = BuildCommand("v-delete-dns-record", new[]
			{
				_options.Username,
				domain,
				recordId,
			}, returnCode: true);
			var response = await PostAsync(body, ct);
			AssertSuccess(response, "v-delete-dns-record");
			_logger.LogInformation("Deleted TXT record {Id} for {Domain}", recordId, domain);
		}
		catch (Exception ex)
		{
			// Best-effort cleanup: log but never throw.
			_logger.LogWarning(ex, "Failed to delete TXT record {Id} for {Domain}", recordId, domain);
		}
	}

	private Dictionary<string, string> BuildCommand(string cmd, IReadOnlyList<string> args, bool returnCode)
	{
		// HestiaCP Access Key authentication: a single 'hash' field of "<accesskey>:<secretkey>".
		var form = new Dictionary<string, string>
		{
			["hash"] = $"{_options.AccessKey}:{_options.SecretKey}",
			["cmd"] = cmd,
		};
		// returncode=yes makes the API return the command's numeric exit code ("0" on success);
		// omit it for list commands so the JSON output is returned instead.
		if (returnCode)
			form["returncode"] = "yes";
		for (var i = 0; i < args.Count; i++)
		{
			form[$"arg{i + 1}"] = args[i];
		}
		return form;
	}

	private async Task<string> PostAsync(Dictionary<string, string> form, CancellationToken ct)
	{
		var baseUrl = _options.BaseUrl.TrimEnd('/');
		using var content = new FormUrlEncodedContent(form);
		using var response = await _http.PostAsync($"{baseUrl}/api/", content, ct);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync(ct);
	}

	private static void AssertSuccess(string responseBody, string command)
	{
		// A successful exec command returns "0"; anything else is an error code/message.
		var trimmed = responseBody.Trim();
		if (trimmed != "0")
		{
			throw new InvalidOperationException($"HestiaCP command '{command}' failed with response '{trimmed}'.");
		}
	}

	/// <summary>
	/// Parses a v-list-dns-records JSON response (object keyed by record id) and returns
	/// the id of the record whose name and value match.
	/// </summary>
	private static string? FindRecordId(string json, string recordName, string value)
	{
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Object)
			return null;

		foreach (var property in doc.RootElement.EnumerateObject())
		{
			var record = property.Value;
			if (record.ValueKind != JsonValueKind.Object)
				continue;

			var type = GetString(record, "TYPE");
			if (!string.Equals(type, "TXT", StringComparison.OrdinalIgnoreCase))
				continue;

			var name = GetString(record, "RECORD");
			var recordValue = GetString(record, "VALUE")?.Trim('"');

			if (string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(recordValue, value, StringComparison.Ordinal))
			{
				// Prefer an explicit ID field, otherwise the property name is the id.
				return GetString(record, "ID") ?? property.Name;
			}
		}
		return null;
	}

	private static string? GetString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
