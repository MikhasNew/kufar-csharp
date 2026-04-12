using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;
using RealEstateMinsk.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=realestate.db"));

builder.Services.AddScoped<ListingService>();
builder.Services.AddScoped<IInvestmentAnalyzer, InvestmentAnalyzer>();
builder.Services.AddScoped<DataSeeder>();

// Фоновый сервис для автоматического сбора данных
builder.Services.AddHostedService<DataCollectionService>();

// KufarScraper с поддержкой логирования (Singleton для reuse HttpClient)
builder.Services.AddSingleton(sp => new KufarScraper(sp.GetService<ILogger<KufarScraper>>()));

// Регистрация HttpClient для геокодера Nominatim (с User-Agent)
builder.Services.AddHttpClient("Nominatim", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "RealEstateMinsk/1.0");
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    
    // Безопасное добавление колонок для существующей БД
    try { db.Database.ExecuteSqlRaw("ALTER TABLE InvestmentScores ADD COLUMN PriceRationale TEXT DEFAULT ''"); } catch {}
    try { db.Database.ExecuteSqlRaw("ALTER TABLE InvestmentScores ADD COLUMN LocationRationale TEXT DEFAULT ''"); } catch {}
    try { db.Database.ExecuteSqlRaw("ALTER TABLE InvestmentScores ADD COLUMN GrowthRationale TEXT DEFAULT ''"); } catch {}
    try { db.Database.ExecuteSqlRaw("ALTER TABLE InvestmentScores ADD COLUMN LiquidityRationale TEXT DEFAULT ''"); } catch {}
    try { db.Database.ExecuteSqlRaw("ALTER TABLE Listings ADD COLUMN IsDistrictAutoDetected INTEGER DEFAULT 0"); } catch {}
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseRouting();

// Razor Pages (нужно для _Host.cshtml)
app.MapRazorPages();

// Minimal API endpoints для сбора данных
app.MapPost("/api/seed", async (DataSeeder seeder) =>
{
    var count = await seeder.SeedTestDataAsync();
    return Results.Ok(new { message = $"Добавлено {count} записей" });
});

app.MapPost("/api/scrape", async (KufarScraper scraper, ListingService listingService, IInvestmentAnalyzer analyzer, IConfiguration config, int? pages = null) =>
{
    try
    {
        var maxPages = pages ?? config.GetValue<int>("DataCollection:MaxPagesPerRun", 5);
        var listings = await scraper.ScrapeAsync(maxPages);
        var saved = await listingService.SaveListingsAsync(listings);
        if (saved > 0) await analyzer.RecalculateAllScoresAsync();
        return Results.Ok(new { received = listings.Count, saved });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/scoring/recalculate", async (IInvestmentAnalyzer analyzer) =>
{
    try
    {
        await analyzer.RecalculateAllScoresAsync();
        return Results.Ok(new { message = "Скоринги пересчитаны" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();