using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// A small Stripe REST client. Stripe takes form-encoded bodies with bracketed keys such as line_items[0][quantity].
public sealed class StripeClient
{
    private const string BaseUrl = "https://api.stripe.com";
    private const long SignatureToleranceSeconds = 300;

    private readonly ForgeOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<StripeClient> _logger;
    private readonly Diagnostics _diagnostics;

    public StripeClient(ForgeOptions options, IHttpClientFactory http, ILogger<StripeClient> logger, Diagnostics diagnostics)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public void RequireConfigured()
    {
        if (!_options.StripeConfigured)
            throw new ApiException(503, "Payments are not set up on this server yet. The site owner needs to add Stripe keys.");
    }

    public Task<JsonObject> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, string? connectedAccount = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = new FormUrlEncodedContent(form) };

        if (idempotencyKey != null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        return SendAsync(request, connectedAccount);
    }

    public Task<JsonObject> DeleteAsync(string path, string? connectedAccount = null)
    {
        return SendAsync(new HttpRequestMessage(HttpMethod.Delete, BaseUrl + path), connectedAccount);
    }

    public Task<JsonObject> GetAsync(string path, string? connectedAccount = null)
    {
        return SendAsync(new HttpRequestMessage(HttpMethod.Get, BaseUrl + path), connectedAccount);
    }

    private async Task<JsonObject> SendAsync(HttpRequestMessage request, string? connectedAccount)
    {
        RequireConfigured();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.StripeSecretKey);

        if (connectedAccount != null)
            request.Headers.Add("Stripe-Account", connectedAccount);

        HttpClient client = _http.CreateClient("stripe");

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            JsonObject? json = JsonBody.ParseObject(body);

            if (response.IsSuccessStatusCode && json != null)
                return json;

            // Stripe's message goes only to the server log, which only the operator reads. Users always get a generic message.
            // Account-level errors (such as a missing business name) have no param or code, so without the message the log says nothing useful.
            string type = json?["error"]?["type"]?.ToString() ?? "unknown";
            string code = json?["error"]?["code"]?.ToString() ?? "";
            string param = json?["error"]?["param"]?.ToString() ?? "";
            string message = json?["error"]?["message"]?.ToString() ?? "";
            string log = json?["error"]?["request_log_url"]?.ToString() ?? "";
            _logger.LogWarning("Stripe {Method} {Path} failed: HTTP {Status} {Type} {Code} param={Param} message={Message} {Log}", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, type, code, param, message, log);
            _diagnostics.Report("Stripe " + type,
                message != "" ? message : $"Stripe returned HTTP {(int)response.StatusCode}.",
                string.Join('\n', new[]
                {
                    $"Request: {request.Method} {request.RequestUri?.AbsolutePath}",
                    $"Status: HTTP {(int)response.StatusCode}",
                    connectedAccount != null ? $"Connected account: {connectedAccount}" : "Account: platform",
                    code != "" ? $"Code: {code}" : null,
                    param != "" ? $"Param: {param}" : null,
                    log != "" ? $"Stripe log: {log}" : null,
                }.Where(line => line != null)));
            throw new ApiException(502, "The payment provider could not complete this request. Please try again in a moment.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Stripe {Path} unreachable: {Type}", request.RequestUri?.AbsolutePath, ex.GetType().Name);
            _diagnostics.Report("Stripe unreachable", $"Could not reach Stripe ({ex.GetType().Name}): {ex.Message}", $"Request: {request.Method} {request.RequestUri?.AbsolutePath}");
            throw new ApiException(502, "The payment provider could not be reached. Please try again in a moment.");
        }
    }

    // Verifies the Stripe-Signature header: t=<unix time>,v1=<hex HMAC-SHA256 of "t.payload">, possibly several v1 values.
    public bool VerifySignature(string payload, string header)
    {
        long timestamp = 0;
        var signatures = new List<byte[]>();

        foreach (string part in header.Split(','))
        {
            int equals = part.IndexOf('=');

            if (equals <= 0)
                continue;

            string key = part[..equals].Trim();
            string value = part[(equals + 1)..].Trim();

            if (key == "t")
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
            else if (key == "v1" && value.Length == 64)
            {
                try
                {
                    signatures.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                }
            }
        }

        if (timestamp == 0 || signatures.Count == 0 || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > SignatureToleranceSeconds)
            return false;

        byte[] signed = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + payload);

        foreach (string secret in _options.StripeWebhookSecrets)
        {
            byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);

            if (signatures.Any(s => CryptographicOperations.FixedTimeEquals(s, expected)))
                return true;
        }

        return false;
    }

    // Checkout on a creator's account shows that creator's own Stripe branding unless the session overrides it, so every session
    // carries the site's name, logo and colour. sellerName adds a line saying who the buyer is paying, because the creator's business
    // name still appears in Stripe's terms and on the card statement.
    public static void AddBranding(List<KeyValuePair<string, string>> form, ForgeOptions options, string? sellerName = null)
    {
        form.Add(new("branding_settings[display_name]", options.BrandName));
        form.Add(new("branding_settings[button_color]", Text.DefaultAccent));
        form.Add(new("branding_settings[border_style]", "rounded"));

        if (options.StripeBrandLogoUrl != null)
        {
            form.Add(new("branding_settings[logo][type]", "url"));
            form.Add(new("branding_settings[logo][url]", options.StripeBrandLogoUrl));
        }

        // With the privacy shield on, creators' statement descriptors start with the site's prefix, so buyers aren't told to look for the creator.
        if (sellerName != null)
            form.Add(new("custom_text[submit][message]", options.PrivacyShieldConfigured
                ? $"You're paying {sellerName} through {options.BrandName}. The charge appears on your statement as {Payments.StatementDescriptor(options, sellerName)}."
                : $"You're paying {sellerName} through {options.BrandName}. The charge appears on your statement under their name."));
    }

    public static string? Str(JsonNode? node)
    {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    public static bool Flag(JsonNode? node)
    {
        return node?.GetValueKind() == JsonValueKind.True;
    }

    public static long Long(JsonNode? node)
    {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long n) ? n : 0;
    }
}
