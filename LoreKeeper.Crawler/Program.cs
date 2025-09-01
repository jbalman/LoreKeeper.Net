using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LoreKeeper.Core;
using LoreKeeper.Storage;
using LoreKeeper.Crawler; // <-- make sure this matches CrawlService namespace

var builder = Host.CreateApplicationBuilder(args);

// Config
var wikis = new[]
{
    "https://defiance-of-the-fall.fandom.com",
    "https://martial-world.fandom.com",
    "https://true-mw.fandom.com"
};
builder.Services.AddSingleton(new CrawlerConfig
{
    Wikis = wikis,
    SeedCategory = "Category:Main",
    DelayMsBetweenCalls = 250
});

builder.Services.AddHttpClient("wiki", client =>
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LoreKeeperCrawler/1.0 (+contact: you@example.com)");
        client.Timeout = TimeSpan.FromSeconds(60);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 5;
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
        // defaults for circuit breaker/timeout are sensible; tweak later if needed
    });

// Storage
builder.Services.AddSingleton(new StorageConfig { ConnectionString = "Data Source=./data/lorekeeper.db" });
builder.Services.AddSingleton<IDbBootstrapper, SqliteBootstrapper>();
builder.Services.AddSingleton<IPagesRepository, PagesRepository>();

// Hosted service
builder.Services.AddHostedService<CrawlService>();

await builder.Build().RunAsync();