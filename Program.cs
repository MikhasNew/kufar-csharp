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
builder.Services.AddSingleton<MinskGeoService>();

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

app.MapGet("/api/test/enrichment", async (AppDbContext db) =>
{
    var total = await db.Listings.CountAsync();
    var autoDetected = await db.Listings.CountAsync(l => l.IsDistrictAutoDetected);
    var unknown = await db.Listings.CountAsync(l => l.District == "Unknown");
    var withCoords = await db.Listings.CountAsync(l => l.Latitude != null);
    
    var samples = await db.Listings
        .Where(l => l.District != "Unknown")
        .Take(10)
        .Select(l => new { 
            l.Id, 
            l.District, 
            l.Location,
            l.IsDistrictAutoDetected,
            HasCoords = l.Latitude != null,
            Score = db.InvestmentScores
                .Where(s => s.ListingId == l.Id)
                .Select(s => new { s.LocationScore, s.LocationRationale, s.TotalScore })
                .FirstOrDefault()
        })
        .ToListAsync();

    return Results.Ok(new { total, autoDetected, unknown, withCoords, samples });
});

// Временный endpoint для тестирования локального геокодера
app.MapGet("/api/test/geocoder", (MinskGeoService geo) =>
{
    var coordTests = new[]
    {
        new { Lat = 53.9045, Lon = 27.5615, Expected = "Центральный" },
        new { Lat = 53.9100, Lon = 27.4500, Expected = "Фрунзенский" },
        new { Lat = 53.9350, Lon = 27.6300, Expected = "Советский" },
        new { Lat = 53.8900, Lon = 27.5500, Expected = "Ленинский" },
        new { Lat = 53.9450, Lon = 27.6700, Expected = "Первомайский" },
        new { Lat = 53.8750, Lon = 27.5700, Expected = "Ленинский" },   // Курасовщина (юг)
        new { Lat = 53.9050, Lon = 27.4800, Expected = "Московский" },
        new { Lat = 53.8950, Lon = 27.6400, Expected = "Партизанский" },
        new { Lat = 53.8800, Lon = 27.6500, Expected = "Заводской" },
    };

    var results = coordTests.Select(t => new
    {
        t.Lat, t.Lon, t.Expected,
        Actual = geo.GetDistrictByCoords(t.Lat, t.Lon),
        Pass = geo.GetDistrictByCoords(t.Lat, t.Lon) == t.Expected
    }).ToList();

    var addrTests = new[]
    {
        new { Address = "Малиновка, ул. Якубовского 45", Expected = "Фрунзенский" },
        new { Address = "Уручье, пр. Независимости 120", Expected = "Первомайский" },
        new { Address = "Чижовка, ул. Чижовская 10", Expected = "Ленинский" },
        new { Address = "Зелёный Луг, ул. Сосновая 5", Expected = "Советский" },
    };

    var addrResults = addrTests.Select(t => new
    {
        t.Address, t.Expected,
        Actual = geo.GetDistrictByAddress(t.Address),
        Pass = geo.GetDistrictByAddress(t.Address) == t.Expected
    }).ToList();

    var totalPassed = results.Count(r => r.Pass) + addrResults.Count(r => r.Pass);
    var totalTests = results.Count + addrResults.Count;

    return Results.Ok(new {
        coordinateTests = results,
        addressTests = addrResults,
        summary = $"{totalPassed}/{totalTests} passed"
    });
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();