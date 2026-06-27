using System.Text.Json;
using AcmeProxy.Configuration;
using AcmeProxy.Data;
using AcmeProxy.Data.Entities;
using AcmeProxy.LetsEncrypt;
using AcmeProxy.Models.Acme;
using AcmeProxy.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcmeProxy.Controllers;

[ApiController]
[Route("letsencrypt/{env}")]
public class AcmeController : ControllerBase
{
	private const string AcmeErrorPrefix = "urn:ietf:params:acme:error:";

	private static readonly HashSet<string> ValidEnvironments = new(StringComparer.OrdinalIgnoreCase) { "staging", "production" };

	private readonly AcmeProxyDbContext _db;
	private readonly NonceService _nonces;
	private readonly ProxyOptions _options;
	private readonly ILetsEncryptClientFactory _leFactory;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<AcmeController> _logger;

	public AcmeController(
		AcmeProxyDbContext db,
		NonceService nonces,
		IOptions<ProxyOptions> options,
		ILetsEncryptClientFactory leFactory,
		IServiceScopeFactory scopeFactory,
		ILogger<AcmeController> logger)
	{
		_db = db;
		_nonces = nonces;
		_options = options.Value;
		_leFactory = leFactory;
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

	private string AcmeBase(string env) => $"{BaseUrl}/letsencrypt/{env}";

	private IActionResult? ValidateEnv(string env)
	{
		if (!ValidEnvironments.Contains(env))
			return Problem(StatusCodes.Status404NotFound, "malformed", $"Unknown environment '{env}'. Use 'staging' or 'production'.");
		return null;
	}

	private void AddReplayNonce() => Response.Headers["Replay-Nonce"] = _nonces.IssueNonce();

	// ---- GET /letsencrypt/{env}/directory -------------------------------------

	[HttpGet("directory")]
	public IActionResult GetDirectory(string env)
	{
		if (ValidateEnv(env) is { } err) return err;
		AddReplayNonce();
		return new JsonResult(new DirectoryModel
		{
			NewNonce = $"{AcmeBase(env)}/new-nonce",
			NewAccount = $"{AcmeBase(env)}/new-account",
			NewOrder = $"{AcmeBase(env)}/new-order",
			RevokeCert = $"{AcmeBase(env)}/revoke-cert",
			KeyChange = $"{AcmeBase(env)}/key-change",
			Meta = new DirectoryMeta
			{
				TermsOfService = "https://letsencrypt.org/documents/LE-SA-v1.3-September-21-2022.pdf",
			},
		});
	}

	// ---- new-nonce -------------------------------------------------------------

	[HttpHead("new-nonce")]
	public IActionResult HeadNewNonce(string env)
	{
		if (ValidateEnv(env) is { } err) return err;
		AddReplayNonce();
		return NoContent();
	}

	[HttpGet("new-nonce")]
	[HttpPost("new-nonce")]
	public IActionResult GetNewNonce(string env)
	{
		if (ValidateEnv(env) is { } err) return err;
		AddReplayNonce();
		return Ok();
	}

	// ---- POST /letsencrypt/{env}/new-account ----------------------------------

	[HttpPost("new-account")]
	public async Task<IActionResult> NewAccount(string env)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		var (jws, error) = await ReadJwsAsync();
		if (error is not null)
			return error;

		if (string.IsNullOrEmpty(jws!.JwkJson))
			return Problem(StatusCodes.Status400BadRequest, "malformed", "new-account requires an embedded JWK.");

		var account = new ClientAccount
		{
			PublicKeyJwkJson = jws.JwkJson,
			CreatedAt = DateTime.UtcNow,
		};
		_db.ClientAccounts.Add(account);
		await _db.SaveChangesAsync();

		AddReplayNonce();
		Response.Headers["Location"] = $"{AcmeBase(env)}/account/{account.Id}";
		return new JsonResult(new AccountResponse
		{
			Status = "valid",
			Contact = new List<string>(),
			Orders = $"{AcmeBase(env)}/orders/{account.Id}",
		})
		{ StatusCode = StatusCodes.Status201Created };
	}

	// ---- POST /letsencrypt/{env}/new-order ------------------------------------

	[HttpPost("new-order")]
	public async Task<IActionResult> NewOrder(string env)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		var (jws, error) = await ReadJwsAsync();
		if (error is not null)
			return error;

		NewOrderRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<NewOrderRequest>(jws!.PayloadJson);
		}
		catch (JsonException)
		{
			return Problem(StatusCodes.Status400BadRequest, "malformed", "Invalid new-order payload.");
		}

		if (request is null || request.Identifiers.Count == 0)
			return Problem(StatusCodes.Status400BadRequest, "malformed", "No identifiers supplied.");

		foreach (var identifier in request.Identifiers)
		{
			if (!DomainWhitelist.IsAllowed(identifier.Value, _options.AllowedDomains))
			{
				return Problem(StatusCodes.Status403Forbidden, "rejectedIdentifier",
					$"Identifier '{identifier.Value}' is not permitted.");
			}
		}

		var primaryDomain = request.Identifiers[0].Value;
		var now = DateTime.UtcNow;

		var order = new ProxyOrder
		{
			Domain = primaryDomain,
			LetsEncryptEnvironment = env.ToLowerInvariant(),
			IdentifiersJson = JsonSerializer.Serialize(request.Identifiers.Select(i => i.Value).ToList()),
			Status = "pending",
			CreatedAt = now,
			UpdatedAt = now,
		};
		var authorization = new ProxyAuthorization
		{
			OrderId = order.Id,
			Domain = primaryDomain,
			Status = "pending",
		};
		var challenge = new ProxyChallenge
		{
			AuthorizationId = authorization.Id,
			Token = AcmeJws.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
			Status = "pending",
		};

		_db.Orders.Add(order);
		_db.Authorizations.Add(authorization);
		_db.Challenges.Add(challenge);
		await _db.SaveChangesAsync();

		AddReplayNonce();
		Response.Headers["Location"] = $"{AcmeBase(env)}/order/{order.Id}";
		return new JsonResult(BuildOrderResponse(order, authorization, env, certificate: null))
		{ StatusCode = StatusCodes.Status201Created };
	}

	// ---- GET/POST-as-GET /letsencrypt/{env}/account/{accountId} ---------------

	[HttpGet("account/{accountId:guid}")]
	[HttpPost("account/{accountId:guid}")]
	public async Task<IActionResult> GetAccount(string env, Guid accountId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("{Method} account {AccountId}", Request.Method, accountId);
		if (HttpMethods.IsPost(Request.Method))
		{
			var (_, err) = await ReadJwsAsync();
			if (err is not null) return err;
		}

		var account = await _db.ClientAccounts.FindAsync(accountId);
		if (account is null)
			return Problem(StatusCodes.Status404NotFound, "accountDoesNotExist", "Account not found.");

		AddReplayNonce();
		Response.Headers["Location"] = $"{AcmeBase(env)}/account/{account.Id}";
		return new JsonResult(new AccountResponse
		{
			Status = "valid",
			Contact = new List<string>(),
			Orders = $"{AcmeBase(env)}/orders/{account.Id}",
		});
	}

	// ---- GET/POST-as-GET /letsencrypt/{env}/orders/{accountId} ----------------

	// This proxy does not maintain client-account-scoped order ownership, so it returns an empty list.
	[HttpGet("orders/{accountId:guid}")]
	[HttpPost("orders/{accountId:guid}")]
	public async Task<IActionResult> GetOrders(string env, Guid accountId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("{Method} orders for account {AccountId}", Request.Method, accountId);
		if (HttpMethods.IsPost(Request.Method))
		{
			var (_, err) = await ReadJwsAsync();
			if (err is not null) return err;
		}

		AddReplayNonce();
		return new JsonResult(new { orders = Array.Empty<string>() });
	}

	// ---- GET/POST-as-GET /letsencrypt/{env}/order/{orderId} -------------------

	[HttpGet("order/{orderId:guid}")]
	[HttpPost("order/{orderId:guid}")]
	public async Task<IActionResult> GetOrder(string env, Guid orderId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("{Method} order {OrderId}", Request.Method, orderId);
		if (HttpMethods.IsPost(Request.Method))
		{
			var (_, err) = await ReadJwsAsync();
			if (err is not null) return err;
		}

		var order = await _db.Orders
			.Include(o => o.Authorization)
			.Include(o => o.Certificate)
			.FirstOrDefaultAsync(o => o.Id == orderId);

		if (order is null)
			return Problem(StatusCodes.Status404NotFound, "malformed", "Order not found.");

		_logger.LogDebug("Order {OrderId} status={Status}", orderId, order.Status);
		AddReplayNonce();
		return new JsonResult(BuildOrderResponse(order, order.Authorization, env, order.Certificate));
	}

	// ---- GET/POST-as-GET /letsencrypt/{env}/authz/{authzId} -------------------

	[HttpGet("authz/{authzId:guid}")]
	[HttpPost("authz/{authzId:guid}")]
	public async Task<IActionResult> GetAuthz(string env, Guid authzId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("{Method} authz {AuthzId}", Request.Method, authzId);
		if (HttpMethods.IsPost(Request.Method))
		{
			var (_, err) = await ReadJwsAsync();
			if (err is not null) return err;
		}

		var authz = await _db.Authorizations
			.Include(a => a.Challenge)
			.FirstOrDefaultAsync(a => a.Id == authzId);

		if (authz is null || authz.Challenge is null)
			return Problem(StatusCodes.Status404NotFound, "malformed", "Authorization not found.");

		AddReplayNonce();
		return new JsonResult(new AuthorizationResponse
		{
			Status = authz.Status,
			Identifier = new Identifier { Type = "dns", Value = authz.Domain },
			Challenges = new List<ChallengeResponse>
			{
				new()
				{
					Type = "dns-01",
					Status = authz.Challenge.Status,
					Url = $"{AcmeBase(env)}/challenge/{authz.Challenge.Id}",
					Token = authz.Challenge.Token,
				},
			},
		});
	}

	// ---- POST /letsencrypt/{env}/challenge/{challengeId} ----------------------

	[HttpPost("challenge/{challengeId:guid}")]
	public async Task<IActionResult> PostChallenge(string env, Guid challengeId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		var (_, error) = await ReadJwsAsync();
		if (error is not null)
			return error;

		var challenge = await _db.Challenges
			.Include(c => c.Authorization)
			.FirstOrDefaultAsync(c => c.Id == challengeId);
		if (challenge is null)
			return Problem(StatusCodes.Status404NotFound, "malformed", "Challenge not found.");

		if (challenge.Status == "pending")
		{
			_logger.LogInformation("Challenge {ChallengeId} accepted; triggering background fulfilment", challengeId);
			TriggerFulfilment(challengeId);
		}
		else
		{
			_logger.LogDebug("Challenge {ChallengeId} POSTed but already {Status}; not re-triggering", challengeId, challenge.Status);
		}

		AddReplayNonce();
		if (challenge.Authorization is not null)
			Response.Headers["Link"] = $"<{AcmeBase(env)}/authz/{challenge.Authorization.Id}>;rel=\"up\"";

		var status = challenge.Status == "pending" ? "processing" : challenge.Status;
		return new JsonResult(new ChallengeResponse
		{
			Type = "dns-01",
			Status = status,
			Url = $"{AcmeBase(env)}/challenge/{challenge.Id}",
			Token = challenge.Token,
		});
	}

	// ---- POST /letsencrypt/{env}/order/{orderId}/finalize ---------------------

	[HttpPost("order/{orderId:guid}/finalize")]
	public async Task<IActionResult> Finalize(string env, Guid orderId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("POST finalize for order {OrderId}", orderId);
		var (jws, error) = await ReadJwsAsync();
		if (error is not null)
			return error;

		var order = await _db.Orders
			.Include(o => o.Authorization)
			.Include(o => o.Certificate)
			.FirstOrDefaultAsync(o => o.Id == orderId);

		if (order is null)
			return Problem(StatusCodes.Status404NotFound, "malformed", "Order not found.");

		if (order.Status != "ready")
			return Problem(StatusCodes.Status403Forbidden, "orderNotReady", "Order is not ready to be finalized.");

		if (string.IsNullOrEmpty(order.LeOrderUrl))
		{
			_logger.LogError("Order {Id} is ready but has no Let's Encrypt order URL", order.Id);
			return Problem(StatusCodes.Status500InternalServerError, "serverInternal",
				"Order is in an inconsistent state and cannot be finalized.");
		}

		FinalizeRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<FinalizeRequest>(jws!.PayloadJson);
		}
		catch (JsonException)
		{
			return Problem(StatusCodes.Status400BadRequest, "malformed", "Invalid finalize payload.");
		}

		if (request is null || string.IsNullOrEmpty(request.Csr))
			return Problem(StatusCodes.Status400BadRequest, "badCSR", "Missing CSR.");

		var csrDer = AcmeJws.Base64UrlDecode(request.Csr);

		string chainPem;
		_logger.LogInformation("Finalizing order {OrderId} via Let's Encrypt {Env} ({CsrBytes} CSR bytes)", orderId, order.LetsEncryptEnvironment, csrDer.Length);
		try
		{
			var le = _leFactory.Get(order.LetsEncryptEnvironment);
			chainPem = await le.FinalizeOrderAsync(order.LeOrderUrl, csrDer, HttpContext.RequestAborted);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Finalization of order {OrderId} failed", orderId);
			return Problem(StatusCodes.Status500InternalServerError, "serverInternal",
				$"Finalization failed: {ex.Message}");
		}

		var certificate = new ProxyCertificate
		{
			OrderId = order.Id,
			CertificateChainPem = chainPem,
			IssuedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddDays(90),
		};
		_db.Certificates.Add(certificate);
		order.Status = "valid";
		order.UpdatedAt = DateTime.UtcNow;
		await _db.SaveChangesAsync();

		AddReplayNonce();
		Response.Headers["Location"] = $"{AcmeBase(env)}/order/{order.Id}";
		return new JsonResult(BuildOrderResponse(order, order.Authorization, env, certificate));
	}

	// ---- GET/POST-as-GET /letsencrypt/{env}/cert/{certId} ---------------------

	[HttpGet("cert/{certId:guid}")]
	[HttpPost("cert/{certId:guid}")]
	public async Task<IActionResult> GetCertificate(string env, Guid certId)
	{
		if (ValidateEnv(env) is { } envErr) return envErr;
		_logger.LogDebug("{Method} certificate {CertId}", Request.Method, certId);
		if (HttpMethods.IsPost(Request.Method))
		{
			var (_, err) = await ReadJwsAsync();
			if (err is not null) return err;
		}

		var cert = await _db.Certificates.FirstOrDefaultAsync(c => c.Id == certId);
		if (cert is null)
			return Problem(StatusCodes.Status404NotFound, "malformed", "Certificate not found.");

		AddReplayNonce();
		return Content(cert.CertificateChainPem, "application/pem-certificate-chain");
	}

	// ---- Unimplemented ---------------------------------------------------------

	[HttpPost("revoke-cert")]
	public IActionResult RevokeCert(string env)
	{
		AddReplayNonce();
		return StatusCode(StatusCodes.Status501NotImplemented);
	}

	[HttpPost("key-change")]
	public IActionResult KeyChange(string env)
	{
		AddReplayNonce();
		return StatusCode(StatusCodes.Status501NotImplemented);
	}

	// ---- Helpers ---------------------------------------------------------------

	private OrderResponse BuildOrderResponse(ProxyOrder order, ProxyAuthorization? authorization, string env, ProxyCertificate? certificate)
	{
		var identifiers = DeserialiseIdentifiers(order);
		var authzUrls = authorization is null
			? new List<string>()
			: new List<string> { $"{AcmeBase(env)}/authz/{authorization.Id}" };

		return new OrderResponse
		{
			Status = order.Status,
			Identifiers = identifiers,
			Authorizations = authzUrls,
			Finalize = $"{AcmeBase(env)}/order/{order.Id}/finalize",
			Certificate = (order.Status == "valid" && certificate is not null)
				? $"{AcmeBase(env)}/cert/{certificate.Id}"
				: null,
		};
	}

	private static List<Identifier> DeserialiseIdentifiers(ProxyOrder order)
	{
		try
		{
			var values = JsonSerializer.Deserialize<List<string>>(order.IdentifiersJson);
			if (values is { Count: > 0 })
				return values.Select(v => new Identifier { Type = "dns", Value = v }).ToList();
		}
		catch (JsonException) { }
		return new List<Identifier> { new() { Type = "dns", Value = order.Domain } };
	}

	private void TriggerFulfilment(Guid challengeId)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				var service = scope.ServiceProvider.GetRequiredService<OrderFulfilmentService>();
				await service.FulfilAsync(challengeId, CancellationToken.None);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Background fulfilment for challenge {Id} threw", challengeId);
			}
		});
	}

	/// <summary>
	/// Reads, parses, verifies (signature + nonce) an ACME JWS POST body.
	/// Returns the parsed request on success, or a populated problem response on failure.
	/// </summary>
	private async Task<(AcmeJws.JwsRequest? jws, IActionResult? error)> ReadJwsAsync()
	{
		string body;
		using (var reader = new StreamReader(Request.Body))
		{
			body = await reader.ReadToEndAsync();
		}

		_logger.LogDebug("Received JWS body ({Bytes} bytes) for {Method} {Path}", body.Length, Request.Method, Request.Path);

		AcmeJws.JwsRequest jws;
		try
		{
			jws = AcmeJws.ParseAndVerify(body, ResolveKidKey);
			_logger.LogDebug("JWS verified: nonce={Nonce} kid={Kid} url={Url} payloadBytes={PayloadBytes}",
				jws.Nonce, jws.Kid, jws.Url, jws.PayloadJson.Length);
		}
		catch (AcmeJws.JwsException ex)
		{
			_logger.LogWarning("Rejected JWS ({Kind}): {Message}", ex.Kind, ex.Message);
			return (null, ex.Kind switch
			{
				AcmeJws.JwsError.Malformed =>
					Problem(StatusCodes.Status400BadRequest, "malformed", "The JWS request was malformed."),
				AcmeJws.JwsError.UnknownAccount =>
					Problem(StatusCodes.Status400BadRequest, "accountDoesNotExist", "The referenced account does not exist."),
				_ =>
					Problem(StatusCodes.Status401Unauthorized, "unauthorized", "The JWS signature could not be verified."),
			});
		}

		if (jws.Nonce is null || !_nonces.ConsumeNonce(jws.Nonce))
			return (null, Problem(StatusCodes.Status400BadRequest, "badNonce", "The nonce was missing, invalid, or already used."));

		return (jws, null);
	}

	/// <summary>
	/// Resolves a stored client account's public JWK from a 'kid' URL such as
	/// "https://host/acme/account/{id}".
	/// </summary>
	private string? ResolveKidKey(string kid)
	{
		var segment = kid.TrimEnd('/');
		var slash = segment.LastIndexOf('/');
		if (slash >= 0)
			segment = segment[(slash + 1)..];

		if (!Guid.TryParse(segment, out var id))
		{
			_logger.LogDebug("kid '{Kid}' did not contain a valid account id", kid);
			return null;
		}

		return _db.ClientAccounts
			.Where(a => a.Id == id)
			.Select(a => a.PublicKeyJwkJson)
			.FirstOrDefault();
	}

	private IActionResult Problem(int statusCode, string acmeError, string detail)
	{
		AddReplayNonce();
		var problem = new ProblemDocument
		{
			Type = AcmeErrorPrefix + acmeError,
			Detail = detail,
		};
		return new ContentResult
		{
			StatusCode = statusCode,
			ContentType = "application/problem+json",
			Content = JsonSerializer.Serialize(problem),
		};
	}
}
