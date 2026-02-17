using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Api.Authentication;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string _apiKeyHeaderName = "X-API-Key";
    private readonly string _apiKey;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _apiKey = configuration.GetValue<string>("Api:ApiKey") ?? string.Empty;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("No API Key configured."));
        }
        
        if (!Request.Headers.ContainsKey(_apiKeyHeaderName))
        {
            return Task.FromResult(AuthenticateResult.Fail("API Key header not found."));
        }

        var providedApiKey = Request.Headers[_apiKeyHeaderName].ToString();

        if (string.IsNullOrEmpty(providedApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API Key is empty."));
        }

        if (!_apiKey.Equals(providedApiKey, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API Key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "API User"),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
} 