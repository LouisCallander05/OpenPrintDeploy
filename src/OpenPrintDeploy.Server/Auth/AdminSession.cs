using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>Keep domain group claims on the server, not in an oversized browser cookie.</summary>
public sealed class AdminSession(IMemoryCache cache) : ITicketStore
{
    public const string Scheme = "AdminSession";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = "opd-session-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        cache.Set(key, ticket, ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.Add(Lifetime));
        return Task.CompletedTask;
    }
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
        => Task.FromResult(cache.Get<AuthenticationTicket>(key));
    public Task RemoveAsync(string key)
    {
        cache.Remove(key);
        return Task.CompletedTask;
    }
}
