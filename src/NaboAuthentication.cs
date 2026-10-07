using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace Nabo.Recipes;

public static class RemoteIdentity
{
    public static ClaimsPrincipal? Parse(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("id", out var value)
            || value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty) return null;
        return new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }, "Nabo"));
    }
}
public class NaboAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IHttpClientFactory clients) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
            return AuthenticateResult.NoResult();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
        request.Headers.Authorization = header;
        using var response = await clients.CreateClient("nabo-auth").SendAsync(request, Context.RequestAborted);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return AuthenticateResult.Fail("Invalid Nabo session.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Nabo authentication unavailable.");
        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Context.RequestAborted));
        var principal = RemoteIdentity.Parse(profile.RootElement);
        if (principal is null) return AuthenticateResult.Fail("Invalid Nabo profile.");
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.CacheControl = "no-store";
        Response.StatusCode = 401; Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(new { message = "Войдите в аккаунт Nabo." });
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Headers.CacheControl = "no-store";
        Response.StatusCode = 403;
        return Response.WriteAsJsonAsync(new { message = "Доступ только для редактора рецептов." });
    }
}
