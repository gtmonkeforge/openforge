using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// In-memory fixed-window limits for sign-in, sign-up and email sending. A restart resets them, which is fine for slowing down guessing.
public sealed class AuthThrottle
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly ForgeOptions _options;
    private DateTime _lastSweep = DateTime.UtcNow;

    public AuthThrottle(ForgeOptions options)
    {
        _options = options;
    }

    private sealed class Window
    {
        public DateTime Start;
        public int Count;
    }

    public string ClientKey(HttpContext context)
    {
        // Behind Cloudflare every request comes from a Cloudflare address, so its header names the real client.
        if (_options.TrustCountryHeader && context.Request.Headers.TryGetValue("CF-Connecting-IP", out var forwarded) && forwarded.ToString() != "")
            return "ip:" + forwarded.ToString();

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    // Counts one attempt against the key and throws 429 once the window's limit is used up.
    public void Hit(string key, int limit, TimeSpan window, string message = "Too many attempts. Wait a few minutes and try again.")
    {
        DateTime now = DateTime.UtcNow;
        Sweep(now);

        Window entry = _windows.GetOrAdd(key, _ => new Window { Start = now });
        bool allowed;

        lock (entry)
        {
            if (now - entry.Start > window)
            {
                entry.Start = now;
                entry.Count = 0;
            }

            entry.Count++;
            allowed = entry.Count <= limit;
        }

        if (!allowed)
            throw new ApiException(429, message);
    }

    public void Reset(string key)
    {
        _windows.TryRemove(key, out _);
    }

    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < TimeSpan.FromMinutes(10))
            return;

        _lastSweep = now;

        foreach (var pair in _windows)
        {
            if (now - pair.Value.Start > TimeSpan.FromHours(2))
                _windows.TryRemove(pair.Key, out _);
        }
    }
}
