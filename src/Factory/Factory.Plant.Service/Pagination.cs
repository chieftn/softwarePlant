using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Factory.Plant.Service;

public sealed class Pagination(IDataProtectionProvider provider, TimeProvider clock)
{
    private readonly IDataProtector protector = provider.CreateProtector("Factory.Plant.Api.Pagination.v1");
    private sealed record Cursor(string Caller, string Route, int PageSize, string AfterId, DateTimeOffset ExpiresAt);

    public string? Read(string? token, ApiCaller caller, string route, int pageSize)
    {
        if (token is null)
            return null;
        if (token.Length is 0 or > 8192)
            throw ApiProblems.Invalid("Invalid continuation token.");
        Cursor? cursor;
        try
        {
            cursor = JsonSerializer.Deserialize<Cursor>(protector.Unprotect(token));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw ApiProblems.Invalid("Invalid continuation token.");
        }
        if (cursor is null || cursor.Caller != caller.Key || cursor.Route != route || cursor.PageSize != pageSize ||
            cursor.ExpiresAt <= clock.GetUtcNow() || string.IsNullOrEmpty(cursor.AfterId))
            throw ApiProblems.Invalid("The continuation token does not match this caller/query or has expired.");
        return cursor.AfterId;
    }

    public string Create(ApiCaller caller, string route, int pageSize, string afterId) =>
        protector.Protect(JsonSerializer.Serialize(new Cursor(caller.Key, route, pageSize, afterId,
            clock.GetUtcNow().AddHours(1))));
}
