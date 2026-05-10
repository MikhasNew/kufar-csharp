using Microsoft.EntityFrameworkCore;
using Npgsql;
using NetTopologySuite.Geometries;
using RealEstateMinsk.Data;
using RealEstateMinsk.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseNetTopologySuite();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(dataSource, o => o.UseNetTopologySuite()));

// Также оставляем обычный AddDbContext для Minimal API и других сервисов, если нужно
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource, o => o.UseNetTopologySuite()));

builder.Services.AddScoped<ListingService>();
builder.Services.AddScoped<IInvestmentAnalyzer, InvestmentAnalyzer>();
builder.Services.AddScoped<DataSeeder>();
builder.Services.AddSingleton<MinskGeoService>();
builder.Services.AddSingleton<UpdateProgressService>();
builder.Services.AddHttpClient<OsmPolygonUpdaterService>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "RealEstateMinsk/1.0");
});

builder.Services.AddHttpClient<PoiUpdaterService>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "RealEstateMinsk/1.0");
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Фоновые сервисы
builder.Services.AddHostedService<DataCollectionService>();
builder.Services.AddHostedService<GeoDataBackgroundService>();

// KufarScraper с поддержкой логирования и локального геокодера (Singleton для reuse HttpClient)
builder.Services.AddSingleton(sp => new KufarScraper(
    sp.GetService<ILogger<KufarScraper>>(), 
    sp.GetRequiredService<MinskGeoService>(),
    sp.GetRequiredService<IConfiguration>()));

// Регистрация HttpClient для геокодера Nominatim (с User-Agent)
builder.Services.AddHttpClient("Nominatim", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "RealEstateMinsk/1.0");
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    try 
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        // Убираем EnsureDeleted(), чтобы данные сохранялись
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS postgis;");
        
        var listingService = scope.ServiceProvider.GetRequiredService<ListingService>();
        _ = listingService.InitializePriceChangesAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при инициализации БД: {ex.Message}");
    }
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
        int totalReceived = 0;
        int totalSaved = 0;
        int totalUpdated = 0;

        // Потоковая обработка: каждая страница сохраняется сразу после загрузки
        foreach (var category in new[] { "Квартира", "Дом" })
        {
            await foreach (var pageListings in scraper.ScrapeEnumerableAsync(maxPages, category))
            {
                totalReceived += pageListings.Count;
                var (newCount, updatedCount, savedListings) = await listingService.SaveListingsAsync(pageListings);
                totalSaved += newCount;
                totalUpdated += updatedCount;

                // Рассчитываем скоринги только для сохранённых объявлений этой страницы
                if (savedListings.Count > 0)
                {
                    await analyzer.UpsertScoresAsync(savedListings);
                }
            }
        }

        return Results.Ok(new { received = totalReceived, saved = totalSaved, updated = totalUpdated });
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

app.MapPost("/api/geo/update-polygons", async (OsmPolygonUpdaterService updater) =>
{
    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minsk_polygons.json");
    await updater.UpdatePolygonsAsync(path);
    return Results.Ok(new { message = "Обновление полигонов запущено/завершено. Проверьте логи." });
});

app.MapGet("/api/migrate-from-sqlite", async (AppDbContext pgDb) =>
{
    var sqliteConnStr = "Data Source=realestate.db";
    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
    optionsBuilder.UseSqlite(sqliteConnStr);
    using var sqliteDb = new AppDbContext(optionsBuilder.Options);

    // Проверка: если в PostgreSQL уже есть данные — отказ
    if (await pgDb.Listings.AnyAsync())
        return Results.BadRequest(new { message = "PostgreSQL уже содержит данные. Миграция отменена." });

    var listings = await sqliteDb.Listings.AsNoTracking().ToListAsync();
    foreach (var l in listings)
    {
        l.Id = 0; // Сбрасываем Id для PostgreSQL auto-increment
        if (l.Latitude.HasValue && l.Longitude.HasValue)
        {
            l.GeoLocation = new Point(l.Longitude.Value, l.Latitude.Value) { SRID = 4326 };
        }
    }
    await pgDb.Listings.AddRangeAsync(listings);
    await pgDb.SaveChangesAsync();

    var scores = await sqliteDb.InvestmentScores.AsNoTracking().ToListAsync();
    foreach (var s in scores) 
    {
        s.Id = 0;
        s.CalculatedAt = DateTime.SpecifyKind(s.CalculatedAt, DateTimeKind.Utc);
    }
    await pgDb.InvestmentScores.AddRangeAsync(scores);
    
    var alerts = await sqliteDb.Alerts.AsNoTracking().ToListAsync();
    foreach (var a in alerts) 
    {
        a.Id = 0;
        a.CreatedAt = DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc);
        if (a.LastTriggered.HasValue)
            a.LastTriggered = DateTime.SpecifyKind(a.LastTriggered.Value, DateTimeKind.Utc);
    }
    
    await pgDb.SaveChangesAsync();

    return Results.Ok(new { 
        message = "Миграция завершена",
        listings = listings.Count,
        scores = scores.Count,
        alerts = alerts.Count
    });
});

app.MapPost("/api/geo/update-poi", async (PoiUpdaterService updater) =>
{
    await updater.UpdateAllPoiAsync();
    return Results.Ok(new { message = "Все POI обновлены (метро, парки, водоёмы, леса)" });
});

app.MapGet("/api/map/data", async (AppDbContext db) =>
{
    var listings = await db.InvestmentScores
        .Include(s => s.Listing)
        .Where(s => s.Listing!.Latitude != null && s.Listing.Longitude != null)
        .Select(s => new {
            lat = s.Listing!.Latitude, lon = s.Listing!.Longitude,
            title = s.Listing.Title, price = s.Listing.PriceUsd,
            pricePerSqm = Math.Round(s.Listing.PricePerSqm, 0),
            area = s.Listing.Area, rooms = s.Listing.Rooms,
            district = s.Listing.District,
            score = Math.Round(s.TotalScore, 1),
            recommendation = s.Recommendation, url = s.Listing.Url
        }).ToListAsync();

    var poi = await db.PointsOfInterest
        .Select(p => new { lat = p.Latitude, lon = p.Longitude, name = p.Name, type = p.Type })
        .ToListAsync();

    object? polygons = null;
    var polygonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minsk_polygons.json");
    if (File.Exists(polygonPath))
    {
        var json = await File.ReadAllTextAsync(polygonPath);
        polygons = System.Text.Json.JsonSerializer.Deserialize<object>(json);
    }

    return Results.Ok(new { listings, poi, polygons });
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();