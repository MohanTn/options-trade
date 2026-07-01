using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using ThetaDesk.Api.Controllers;
using ThetaDesk.Data;
using ThetaDesk.Api.Kite;
using ThetaDesk.Api.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/thetadesk-.log", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Database — PostgreSQL (local container) with SQLite fallback
var connStr = builder.Configuration.GetConnectionString("Db");
if (!string.IsNullOrEmpty(connStr) && connStr.Contains("Host="))
{
    builder.Services.AddDbContext<ThetaDeskDbContext>(o => o.UseNpgsql(connStr));
}
else
{
    builder.Services.AddDbContext<ThetaDeskDbContext>(o =>
        o.UseSqlite(connStr ?? "Data Source=thetadesk.db"));
}

// Redis
var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddMemoryCache();

// JWT auth
var jwtKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey not configured");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false
        };
    });
builder.Services.AddAuthorization();

// Application services
// Paper trading: keep live Zerodha market data but simulate order execution.
if (builder.Configuration.GetValue<bool>("Kite:PaperTrading"))
{
    builder.Services.AddSingleton<KiteClient>();
    builder.Services.AddSingleton<IKiteClient>(sp => new PaperKiteClient(
        sp.GetRequiredService<KiteClient>(),
        sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<PaperKiteClient>>()));
}
else
{
    builder.Services.AddSingleton<IKiteClient, KiteClient>();
}
builder.Services.AddSingleton<KillSwitchState>();
builder.Services.AddScoped<SignalEngine>();
builder.Services.AddScoped<SizingEngine>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<BrokerSyncService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ThetaDeskDbContext>("db")
    .AddRedis(redisConn, "redis");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// CORS — allow the React dev server, plus any extra origins from config
// (e.g. Cors__AllowedOrigins=http://192.168.0.184:5173 for LAN access to the API directly)
var corsOrigins = new[] { "http://localhost:5173", "http://localhost:3000" }
    .Concat((builder.Configuration["Cors:AllowedOrigins"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .Distinct()
    .ToArray();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ThetaDeskDbContext>();
    db.Database.Migrate();
    await SeedFundAsync(db);
    await SeedStrategiesAsync(db); // separate so defaults also appear on DBs created before strategies existed
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseExceptionHandler(ex => ex.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error" });
}));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

// Login endpoint — issues a JWT for the single operator
app.MapPost("/api/v1/auth/login", (LoginRequest req, IConfiguration config) =>
{
    var pass = config["Operator:Password"] ?? "changeme";
    if (req.Password != pass)
        return Results.Unauthorized();

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        expires: DateTime.UtcNow.AddHours(12),
        signingCredentials: creds);
    var tokenStr = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = tokenStr });
});

app.Run();

static async Task SeedFundAsync(ThetaDeskDbContext db)
{
    if (await db.Funds.AnyAsync()) return;

    var fund = new ThetaDesk.Domain.Entities.Fund
    {
        Name = "ThetaDesk NIFTY 50 Theta-Harvesting Fund",
        StartingCapital = 850000m,
        CashBalance = 850000m,
        CurrentNav = 850000m,
    };
    db.Funds.Add(fund);

    var defaultLimits = new[]
    {
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Portfolio, Metric = "MarginUtilPct", UpperBound = 70, Hard = true },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Position, Metric = "MaxLoss", UpperBound = 17000, Hard = true },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Portfolio, Metric = "DrawdownPct", UpperBound = 6, Hard = true },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Portfolio, Metric = "NetDelta", LowerBound = -10, UpperBound = 10, Hard = false },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Portfolio, Metric = "NetGamma", LowerBound = -1.2m, Hard = false },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Portfolio, Metric = "NetVega", LowerBound = -1600, UpperBound = 0, Hard = false },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Position, Metric = "TestedSideDelta", UpperBound = 0.30m, Hard = false },
        new ThetaDesk.Domain.Entities.RiskLimit { FundId = fund.Id, Scope = ThetaDesk.Domain.Enums.LimitScope.Position, Metric = "MarginUtilSoft", UpperBound = 60, Hard = false },
    };
    db.RiskLimits.AddRange(defaultLimits);
    await db.SaveChangesAsync();
}

// Idempotently seeds the default regime strategies if none exist yet (won't clobber operator edits).
static async Task SeedStrategiesAsync(ThetaDeskDbContext db)
{
    if (await db.StrategyConfigs.AnyAsync()) return;
    var fund = await db.Funds.FirstOrDefaultAsync();
    if (fund == null) return;
    db.StrategyConfigs.AddRange(DefaultStrategies(fund.Id));
    await db.SaveChangesAsync();
}

// Builds the three default regime strategies per the desk's playbook.
static ThetaDesk.Domain.Entities.StrategyConfig[] DefaultStrategies(Guid fundId)
{
    static ThetaDesk.Domain.Entities.StrategyLeg Leg(ThetaDesk.Domain.Enums.OptionType type, ThetaDesk.Domain.Enums.Side side, decimal delta, ThetaDesk.Domain.Enums.ExpiryRank expiry = ThetaDesk.Domain.Enums.ExpiryRank.Near) =>
        new() { OptionType = type, Side = side, TargetDelta = delta, Expiry = expiry };

    var CE = ThetaDesk.Domain.Enums.OptionType.CE;
    var PE = ThetaDesk.Domain.Enums.OptionType.PE;
    var Buy = ThetaDesk.Domain.Enums.Side.Buy;
    var Sell = ThetaDesk.Domain.Enums.Side.Sell;
    var Far = ThetaDesk.Domain.Enums.ExpiryRank.Far;

    return
    [
        // VIX 10–12: double calendar — sell near-month, buy far-month ATM; exit at 7 DTE or 2× debit paid.
        new ThetaDesk.Domain.Entities.StrategyConfig
        {
            FundId = fundId, Name = "Low-VIX Double Calendar (10–12)", Strategy = ThetaDesk.Domain.Enums.StrategyType.DoubleCalendar,
            VixMin = 10, VixMax = 12, EntryDteMin = 21, EntryDteMax = 44, SizingPct = 100, GttEnabled = false,
            ProfitTargetPct = 200, TargetExitDte = 7, AdjustTriggerDelta = 0.30m,
            Legs = [Leg(PE, Sell, 0.50m), Leg(CE, Sell, 0.50m), Leg(PE, Buy, 0.50m, Far), Leg(CE, Buy, 0.50m, Far)]
        },
        // VIX 12–18: short strangle on the near monthly expiry; exit at 21 DTE or 50% of credit received.
        new ThetaDesk.Domain.Entities.StrategyConfig
        {
            FundId = fundId, Name = "Mid-VIX Short Strangle (12–18)", Strategy = ThetaDesk.Domain.Enums.StrategyType.ShortStrangle,
            VixMin = 12, VixMax = 18, EntryDteMin = 21, EntryDteMax = 44, SizingPct = 100, GttEnabled = true, GttPremiumPct = 200,
            ProfitTargetPct = 50, TargetExitDte = 21, AdjustTriggerDelta = 0.30m,
            Legs = [Leg(PE, Sell, 0.16m), Leg(CE, Sell, 0.16m)]
        },
        // VIX 18+: iron condor at 45+ DTE; exit at 21 DTE or 50% of the premium collected.
        new ThetaDesk.Domain.Entities.StrategyConfig
        {
            FundId = fundId, Name = "High-VIX Iron Condor (18+)", Strategy = ThetaDesk.Domain.Enums.StrategyType.IronCondor,
            VixMin = 18, VixMax = 100, EntryDteMin = 45, EntryDteMax = 60, SizingPct = 75, GttEnabled = false,
            ProfitTargetPct = 50, TargetExitDte = 21, AdjustTriggerDelta = 0.30m,
            Legs = [Leg(PE, Sell, 0.16m), Leg(CE, Sell, 0.16m), Leg(PE, Buy, 0.05m), Leg(CE, Buy, 0.05m)]
        },
    ];
}

record LoginRequest(string Password);
