using System.Diagnostics;

namespace AcmeProxy.Services;

/// <summary>
/// A delegating handler that logs outbound HTTP requests and responses (including bodies)
/// at Debug level. Used to make the HestiaCP and Let's Encrypt conversations visible when
/// diagnosing issues. Bodies are only read when Debug logging is enabled.
/// </summary>
public class LoggingHttpMessageHandler : DelegatingHandler
{
	private readonly ILogger _logger;
	private readonly string _label;

	public LoggingHttpMessageHandler(ILogger logger, string label)
	{
		_logger = logger;
		_label = label;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		var debug = _logger.IsEnabled(LogLevel.Debug);

		if (debug)
		{
			var requestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
			_logger.LogDebug("[{Label}] --> {Method} {Uri}\n{Body}", _label, request.Method, request.RequestUri, requestBody);
		}

		var stopwatch = Stopwatch.StartNew();
		var response = await base.SendAsync(request, ct);
		stopwatch.Stop();

		if (debug)
		{
			var responseBody = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
			_logger.LogDebug("[{Label}] <-- {Status} ({Elapsed} ms)\n{Body}",
				_label, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, responseBody);
		}
		else
		{
			_logger.LogInformation("[{Label}] {Method} {Uri} -> {Status} ({Elapsed} ms)",
				_label, request.Method, request.RequestUri, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
		}

		return response;
	}
}
