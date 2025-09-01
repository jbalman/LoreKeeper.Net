using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using LoreKeeper.Core;
using LoreKeeper.Storage;
using LoreKeeper.Crawler;

var builder = Host.CreateApplicationBuilder(args);

// Bind config
var crawlerConfig = builder.Configuration
    .GetSection("Crawler")
    .Get<CrawlerConfig>() ?? new CrawlerConfig();

var storageConfig = builder.Configuration
    .GetSection("Storage")
    .Get<StorageConfig>() ?? new StorageConfig();

builder.Services.AddSingleton(crawlerConfig);
builder.Services.AddSingleton(storageConfig);

// HttpClient with resilience
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
    });

// Storage + Repos
builder.Services.AddSingleton<IDbBootstrapper, SqliteBootstrapper>();
builder.Services.AddSingleton<IPagesRepository, PagesRepository>();

// Hosted service
builder.Services.AddHostedService<CrawlService>();

await builder.Build().RunAsync();