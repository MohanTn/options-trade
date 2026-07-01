using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using ThetaDesk.Data;
using ThetaDesk.Workers;

var builder = Host.CreateApplicationBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Db");
if (!string.IsNullOrEmpty(connStr) && connStr.Contains("Host="))
    builder.Services.AddDbContext<ThetaDeskDbContext>(o => o.UseNpgsql(connStr));
else
    builder.Services.AddDbContext<ThetaDeskDbContext>(o => o.UseSqlite(connStr ?? "Data Source=thetadesk.db"));

var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));

builder.Services.AddSingleton<KillSwitchShim>();
builder.Services.AddHostedService<MarketDataWorker>();
builder.Services.AddHostedService<LifecycleManagerWorker>();

var host = builder.Build();
host.Run();
