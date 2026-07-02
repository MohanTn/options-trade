using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace ThetaDesk.Api.Kite;

public class KiteClient(IConfiguration config, IMemoryCache cache, IConnectionMultiplexer redis, ILogger<KiteClient> logger) : IKiteClient
{
    private const string BaseUrl = "https://api.kite.trade";
    private const string LoginUrl = "https://kite.zerodha.com/connect/login";
    private const string SessionKey = "kite_access_token";
    private const string SessionExpKey = "kite_token_expiry";

    private readonly string _apiKey = config["Kite:ApiKey"] ?? throw new InvalidOperationException("Kite:ApiKey not configured");
    private readonly string _apiSecret = config["Kite:ApiSecret"] ?? throw new InvalidOperationException("Kite:ApiSecret not configured");

    private string? AccessToken
    {
        get
        {
            // Fast path: in-process cache
            if (cache.TryGetValue<string>(SessionKey, out var t) && t != null) return t;
            // Fallback: Redis (survives container restarts)
            var db = redis.GetDatabase();
            var tok = (string?)db.StringGet(SessionKey);
            if (tok == null) return null;
            // Re-warm in-process cache; expiry unknown here so use 1 hour (token still valid if Redis has it)
            cache.Set(SessionKey, tok, TimeSpan.FromHours(1));
            return tok;
        }
    }

    public Task<KiteSessionStatus> GetSessionStatusAsync()
    {
        var token = AccessToken;
        cache.TryGetValue<DateTime>(SessionExpKey, out var exp);
        if (exp == default)
        {
            var db = redis.GetDatabase();
            var ttl = db.KeyTimeToLive(SessionKey);
            if (ttl.HasValue) exp = DateTime.UtcNow.Add(ttl.Value);
        }
        var loginUrl = $"{LoginUrl}?api_key={_apiKey}&v=3";
        return Task.FromResult(new KiteSessionStatus(token != null, token == null ? loginUrl : null, token != null ? exp : null));
    }

    public async Task<string> ExchangeTokenAsync(string requestToken, CancellationToken ct = default)
    {
        var checksum = Sha256($"{_apiKey}{requestToken}{_apiSecret}");
        using var client = MakeClient(null);
        var resp = await client.PostAsync("/session/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["request_token"] = requestToken,
                ["checksum"] = checksum
            }), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var accessToken = json.GetProperty("data").GetProperty("access_token").GetString()!;
        // Kite tokens expire at ~06:00 IST the next day; compute expiry in UTC
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
        var expIst = nowIst.Date.AddDays(1).AddHours(6);
        var exp = TimeZoneInfo.ConvertTimeToUtc(expIst, ist);
        var ttl = exp - DateTime.UtcNow;
        // Persist in both Redis (durable) and in-process cache (fast)
        var db = redis.GetDatabase();
        db.StringSet(SessionKey, accessToken, ttl);
        cache.Set(SessionKey, accessToken, exp);
        cache.Set(SessionExpKey, exp, exp);
        logger.LogInformation("Kite session token acquired, valid until {Expiry}", exp);
        return accessToken;
    }

    public Task ClearSessionAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        db.KeyDelete(SessionKey);
        cache.Remove(SessionKey);
        cache.Remove(SessionExpKey);
        logger.LogInformation("Kite session cleared by operator");
        return Task.CompletedTask;
    }

    public async Task<decimal> GetIndiaVixAsync(CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var resp = await client.GetFromJsonAsync<JsonElement>("/quote?i=NSE:INDIA VIX", ct);
        return (decimal)resp.GetProperty("data").GetProperty("NSE:INDIA VIX").GetProperty("last_price").GetDouble();
    }

    private const string InstrumentsCacheKey = "kite_nifty_instruments";

    public async Task<IReadOnlyList<KiteInstrument>> GetNiftyInstrumentsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue<IReadOnlyList<KiteInstrument>>(InstrumentsCacheKey, out var cached) && cached != null)
            return cached;

        using var client = MakeClient(AccessToken);
        var csv = await client.GetStringAsync("/instruments/NFO", ct);
        var instruments = ParseInstrumentsCsv(csv);
        cache.Set(InstrumentsCacheKey, instruments, TimeSpan.FromMinutes(30));
        return instruments;
    }

    public async Task<IReadOnlyList<KiteQuote>> GetQuotesAsync(IEnumerable<long> tokens, CancellationToken ct = default)
    {
        var query = string.Join("&", tokens.Select(t => $"i={t}"));
        using var client = MakeClient(AccessToken);
        var resp = await client.GetFromJsonAsync<JsonElement>($"/quote?{query}", ct);
        var data = resp.GetProperty("data");
        var results = new List<KiteQuote>();
        foreach (var prop in data.EnumerateObject())
        {
            var v = prop.Value;
            long token = v.GetProperty("instrument_token").GetInt64();
            results.Add(new KiteQuote(
                token,
                (decimal)v.GetProperty("last_price").GetDouble(),
                (decimal)v.GetProperty("depth").GetProperty("buy")[0].GetProperty("price").GetDouble(),
                (decimal)v.GetProperty("depth").GetProperty("sell")[0].GetProperty("price").GetDouble(),
                v.GetProperty("oi").GetInt64(),
                v.TryGetProperty("iv", out var ivEl) ? (decimal)ivEl.GetDouble() : 0,
                v.TryGetProperty("volume", out var volEl) ? volEl.GetInt64() : 0));
        }
        return results;
    }

    public async Task<KiteMargin> GetBasketMarginAsync(IEnumerable<BasketLeg> legs, CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var body = JsonSerializer.Serialize(legs.Select(l => new
        {
            exchange = l.Exchange, tradingsymbol = l.Symbol, transaction_type = l.TransactionType,
            quantity = l.Qty, product = l.Product, order_type = l.OrderType
        }));
        var resp = await client.PostAsync("/margins/basket",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var d = json.GetProperty("data").GetProperty("total");
        return new KiteMargin(
            (decimal)d.GetProperty("span").GetDouble(),
            (decimal)d.GetProperty("exposure").GetDouble(),
            (decimal)d.GetProperty("total").GetDouble());
    }

    public async Task<KiteOrderResult> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var form = new Dictionary<string, string>
        {
            ["exchange"] = req.Exchange, ["tradingsymbol"] = req.Symbol,
            ["transaction_type"] = req.TransactionType, ["quantity"] = req.Qty.ToString(),
            ["product"] = req.Product, ["order_type"] = req.OrderType,
            ["validity"] = "DAY"
        };
        if (req.Price.HasValue) form["price"] = req.Price.Value.ToString("F2");
        if (req.Tag != null) form["tag"] = req.Tag;
        var resp = await client.PostAsync("/orders/regular", new FormUrlEncodedContent(form), ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!resp.IsSuccessStatusCode)
            return new KiteOrderResult("", false, json.GetProperty("message").GetString());
        return new KiteOrderResult(json.GetProperty("data").GetProperty("order_id").GetString()!, true, null);
    }

    public async Task<KiteGttResult> PlaceGttStopAsync(GttRequest req, CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var body = JsonSerializer.Serialize(new
        {
            type = "single",
            condition = new { exchange = req.Exchange, tradingsymbol = req.Symbol, trigger_values = new[] { req.TriggerPrice }, last_price = req.TriggerPrice },
            orders = new[] { new { exchange = req.Exchange, tradingsymbol = req.Symbol, transaction_type = req.TransactionType, quantity = req.Qty, order_type = "MARKET", product = "NRML" } }
        });
        var resp = await client.PostAsync("/gtt/triggers", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!resp.IsSuccessStatusCode)
            return new KiteGttResult("", false, json.GetProperty("message").GetString());
        return new KiteGttResult(json.GetProperty("data").GetProperty("trigger_id").ToString(), true, null);
    }

    public async Task<bool> CancelGttAsync(string gttId, CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var resp = await client.DeleteAsync($"/gtt/triggers/{gttId}", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var resp = await client.DeleteAsync($"/orders/regular/{orderId}", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<decimal> GetFundsAsync(CancellationToken ct = default)
    {
        var summary = await GetMarginSummaryAsync(ct);
        return summary.TotalCapital;
    }

    public async Task<MarginSummary> GetMarginSummaryAsync(CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var resp = await client.GetFromJsonAsync<JsonElement>("/user/margins", ct);
        var equity = resp.GetProperty("data").GetProperty("equity");
        var available = equity.GetProperty("available");
        var cash = (decimal)available.GetProperty("cash").GetDouble();
        var collateral = (decimal)available.GetProperty("collateral").GetDouble();
        var liveBalance = (decimal)available.GetProperty("live_balance").GetDouble();
        var utilised = equity.GetProperty("utilised");
        var span = (decimal)utilised.GetProperty("span").GetDouble();
        var exposure = (decimal)utilised.GetProperty("exposure").GetDouble();
        var optionPremium = utilised.TryGetProperty("option_premium", out var opEl)
            ? (decimal)opEl.GetDouble() : 0;
        return new MarginSummary(
            TotalCapital: cash + collateral,
            AvailableBalance: liveBalance,
            Span: span,
            Exposure: exposure,
            OptionPremium: optionPremium,
            Collateral: collateral
        );
    }

    public async Task<decimal> GetNiftySpotAsync(CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var resp = await client.GetFromJsonAsync<JsonElement>("/quote?i=NSE:NIFTY 50", ct);
        return (decimal)resp.GetProperty("data").GetProperty("NSE:NIFTY 50").GetProperty("last_price").GetDouble();
    }

    public async Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct = default)
    {
        using var client = MakeClient(AccessToken);
        var json = await client.GetFromJsonAsync<JsonElement>("/portfolio/positions", ct);
        var data = json.GetProperty("data").GetProperty("net");
        var results = new List<BrokerPosition>();
        foreach (var p in data.EnumerateArray())
        {
            var qty = p.GetProperty("quantity").GetInt32();
            if (qty == 0) continue;
            results.Add(new BrokerPosition(
                p.GetProperty("instrument_token").GetInt64(),
                p.GetProperty("tradingsymbol").GetString()!,
                p.GetProperty("exchange").GetString()!,
                p.GetProperty("product").GetString()!,
                qty,
                (decimal)p.GetProperty("average_price").GetDouble(),
                (decimal)p.GetProperty("last_price").GetDouble(),
                (decimal)p.GetProperty("unrealised").GetDouble(),
                (decimal)p.GetProperty("realised").GetDouble()));
        }
        return results;
    }

    private HttpClient MakeClient(string? token)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
        client.DefaultRequestHeaders.Add("X-Kite-Apikey", _apiKey);
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{_apiKey}:{token}");
        return client;
    }

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLower();
    }

    private static IReadOnlyList<KiteInstrument> ParseInstrumentsCsv(string csv)
    {
        var result = new List<KiteInstrument>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 10) continue;
            // Kite CSV: instrument_type col[9] is now "CE"/"PE" directly (was "OPTIDX")
            var optType = cols[9].Trim();
            if (optType != "CE" && optType != "PE") continue;
            // name col[3] may be quoted in the CSV: "NIFTY" — strip surrounding double-quotes
            var name = cols[3].Trim().Trim('"');
            if (!name.Equals("NIFTY", StringComparison.OrdinalIgnoreCase)) continue;
            if (!long.TryParse(cols[0], out var token)) continue;
            if (!decimal.TryParse(cols[6], out var strike)) continue;
            if (!DateOnly.TryParse(cols[5], out var expiry)) continue;
            // Real exchange lot size from the instrument dump; 0 means "unknown" — sizing uses Fund.LotSize, not this.
            if (!int.TryParse(cols[8], out var lotSize)) lotSize = 0;
            result.Add(new KiteInstrument(token, cols[2].Trim(), strike, expiry, optType, lotSize));
        }
        return result;
    }
}
