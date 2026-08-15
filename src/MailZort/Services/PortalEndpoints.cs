using System.Security.Claims;
using MailZort.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MailZort.Services;

/// <summary>
/// Sign-in and sign-out have to run on a real HTTP request - a Blazor circuit cannot write
/// the auth cookie - so the login form posts here.
/// </summary>
public static class PortalEndpoints
{
    public static void MapPortalEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext http, IUserStore users, LoginThrottle throttle) =>
        {
            var form = await http.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var throttleKey = $"{http.Connection.RemoteIpAddress}|{username}";
            if (throttle.IsLockedOut(throttleKey, out var retryAfter))
            {
                return Results.Redirect($"/login?locked={(int)Math.Ceiling(retryAfter.TotalMinutes)}");
            }

            var user = await users.ValidateAsync(username, password);
            if (user == null)
            {
                throttle.RecordFailure(throttleKey);
                return Results.Redirect("/login?error=1");
            }

            throttle.RecordSuccess(throttleKey);

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id.ToString())
            };

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = true });

            await users.RecordLoginAsync(user.Username);

            // Still on the seeded admin/admin - send them straight to the change form.
            if (user.MustChangePassword)
            {
                return Results.Redirect("/account?first=1");
            }

            return Results.Redirect(IsLocalPath(returnUrl) ? returnUrl : "/");
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).DisableAntiforgery();
    }

    /// <summary>Only ever redirect inside this app - never to a URL an attacker supplied.</summary>
    private static bool IsLocalPath(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//");
}
