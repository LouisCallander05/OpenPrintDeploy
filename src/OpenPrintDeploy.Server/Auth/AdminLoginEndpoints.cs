using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using OpenPrintDeploy.Server.Directory;

namespace OpenPrintDeploy.Server.Auth;

public static class AdminLoginEndpoints
{
    public static void MapAdminLogin(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync).AllowAnonymous()
            .RequireRateLimiting("admin-login")
            .WithMetadata(new RequestSizeLimitAttribute(8192));
        app.MapPost("/auth/logout", LogoutAsync).AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(8192));
    }

    private static async Task<IResult> LoginAsync(HttpContext http, IAntiforgery antiforgery,
        IAdminCredentials credentials, LoginThrottle throttle, AdminAccessEvaluator access, AuthSchemes schemes)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (schemes.Admin != AdminSession.Scheme) return Results.NotFound();
        // Refuse to read domain passwords over plaintext, even if TLS provisioning failed.
        if (!http.Request.IsHttps) return Results.BadRequest("Use HTTPS to sign in.");
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest("The form expired. Reload the sign-in page and try again."); }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var returnUrl = LocalReturnUrl(form["returnUrl"]);
        IResult Failure(string error) => Results.LocalRedirect(QueryHelpers.AddQueryString("/login",
            new Dictionary<string, string?> { ["error"] = error, ["ReturnUrl"] = returnUrl }));

        if (username.Length is 0 or > 256 || password.Length is 0 or > 1024) return Failure("credentials");
        var key = DirectoryUsername.Normalize(username);
        if (throttle.IsLockedOut(key, out _)) return Failure("throttled");

        var result = await credentials.ValidateAsync(username, password, http.RequestAborted);
        if (result.Principal is null)
        {
            if (result.InvalidCredentials)
            {
                throttle.RecordFailure(key);
                return Failure("credentials");
            }
            return Failure("unavailable");
        }
        throttle.RecordSuccess(key);
        if (!await access.IsAdminAsync(result.Principal, http.RequestAborted)) return Failure("access");

        await http.SignInAsync(AdminSession.Scheme, result.Principal, new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = false,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(AdminSession.Lifetime),
        });
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IAntiforgery antiforgery, AuthSchemes schemes)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (schemes.Admin != AdminSession.Scheme) return Results.NotFound();
        if (!http.Request.IsHttps) return Results.BadRequest("Use HTTPS to sign out.");
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest("Reload the page and try signing out again."); }
        await http.SignOutAsync(AdminSession.Scheme);
        return Results.LocalRedirect("/login");
    }

    public static string LocalReturnUrl(string? url)
        => !string.IsNullOrEmpty(url) && url[0] == '/'
            && (url.Length == 1 || url[1] is not ('/' or '\\'))
            && !url.Any(char.IsControl) && !url.Contains('\\') ? url : "/";
}
