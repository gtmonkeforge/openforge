using System;
using System.Collections.Concurrent;
using System.Linq;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// Rendered emails waiting to be shown in the admin panel's preview frame. The page's own CSP blocks the inline styles
// emails rely on, so the frame loads each preview from its own URL, which carries a looser policy and no scripts.
public sealed class EmailPreviews
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (string UserId, string Html, DateTime Expires)> _items = new();

    public string Add(string userId, string html)
    {
        DateTime now = DateTime.UtcNow;

        foreach (var expired in _items.Where(pair => pair.Value.Expires < now).ToList())
            _items.TryRemove(expired.Key, out _);

        string token = Text.UrlToken(18);
        _items[token] = (userId, html, now.Add(Lifetime));
        return token;
    }

    public string? Get(string token, string userId)
    {
        return _items.TryGetValue(token, out var item) && item.UserId == userId && item.Expires > DateTime.UtcNow ? item.Html : null;
    }
}
