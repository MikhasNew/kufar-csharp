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
        
        db.Database.OpenConnection();
        db.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS postgis;");
        db.Database.EnsureCreated();

        // Явно создаем таблицу PointsOfInterest, так как EnsureCreated не добавляет новые таблицы в существующую схему
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""PointsOfInterest"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""Name"" TEXT NOT NULL,
                ""Type"" TEXT NOT NULL,
                ""GeoLocation"" geometry(Point, 4326),
                ""Latitude"" DOUBLE PRECISION NOT NULL,
                ""Longitude"" DOUBLE PRECISION NOT NULL,
                ""Extra"" TEXT,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_PointsOfInterest_Type"" ON ""PointsOfInterest"" (""Type"");
        ");

        // Проверяем наличие колонки GeoLocation в Listings (могла отсутствовать, если база создана до Фазы 1)
        try {
            db.Database.ExecuteSqlRaw("ALTER TABLE \"Listings\" ADD COLUMN IF NOT EXISTS \"GeoLocation\" geometry(Point, 4326);");
            
            // Конвертируем текстовые даты в timestamp, если они еще текстовые
            db.Database.ExecuteSqlRaw(@"
                DO $$ 
                BEGIN 
                    IF (SELECT data_type FROM information_schema.columns WHERE table_name = 'Listings' AND column_name = 'CreatedAt') = 'text' THEN
                        ALTER TABLE ""Listings"" ALTER COLUMN ""CreatedAt"" TYPE timestamp with time zone USING ""CreatedAt""::timestamp with time zone;
                    END IF;
                    IF (SELECT data_type FROM information_schema.columns WHERE table_name = 'Listings' AND column_name = 'ScrapedAt') = 'text' THEN
                        ALTER TABLE ""Listings"" ALTER COLUMN ""ScrapedAt"" TYPE timestamp with time zone USING ""ScrapedAt""::timestamp with time zone;
                    END IF;
                    IF (SELECT data_type FROM information_schema.columns WHERE table_name = 'PriceHistories' AND column_name = 'RecordedAt') = 'text' THEN
                        ALTER TABLE ""PriceHistories"" ALTER COLUMN ""RecordedAt"" TYPE timestamp with time zone USING ""RecordedAt""::timestamp with time zone;
                    END IF;
                END $$;
            ");
        } catch (Exception ex) { 
            Console.WriteLine($"Предупреждение при миграции колонок: {ex.Message}");
        }
        
        var listingService = scope.ServiceProvider.GetRequiredService<ListingService>();
        _ = listingService.InitializePriceChangesAsync();
        await RealEstateMinsk.Scratch.StatsCheck.Run(scope.ServiceProvider);
        await RealEstateMinsk.Scratch.FilterCheck.Run(db);
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
    if (!File.Exists("realestate.db"))
        return Results.NotFound(new { message = "Файл realestate.db не найден" });

    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
    optionsBuilder.UseSqlite(sqliteConnStr);
    using var sqliteDb = new AppDbContext(optionsBuilder.Options);

    if (await pgDb.Listings.AnyAsync())
        return Results.BadRequest(new { message = "PostgreSQL уже содержит данные. Миграция отменена." });

    // 1. Listings
    var oldListings = await sqliteDb.Listings.AsNoTracking().ToListAsync();
    var idMap = new Dictionary<int, int>();

    foreach (var l in oldListings)
    {
        var oldId = l.Id;
        l.Id = 0; 
        l.ScrapedAt = DateTime.SpecifyKind(l.ScrapedAt, DateTimeKind.Utc);
        l.CreatedAt = DateTime.SpecifyKind(l.CreatedAt, DateTimeKind.Utc);
        
        if (l.Latitude.HasValue && l.Longitude.HasValue)
        {
            l.GeoLocation = new Point(l.Longitude.Value, l.Latitude.Value) { SRID = 4326 };
        }
        
        await pgDb.Listings.AddAsync(l);
        await pgDb.SaveChangesAsync(); // Сохраняем по одному, чтобы получить новый Id
        idMap[oldId] = l.Id;
    }

    // 2. PriceHistories
    var histories = await sqliteDb.PriceHistories.AsNoTracking().ToListAsync();
    foreach (var h in histories)
    {
        if (idMap.TryGetValue(h.ListingId, out var newId))
        {
            h.Id = 0;
            h.ListingId = newId;
            h.RecordedAt = DateTime.SpecifyKind(h.RecordedAt, DateTimeKind.Utc);
            await pgDb.PriceHistories.AddAsync(h);
        }
    }

    // 3. InvestmentScores
    var scores = await sqliteDb.InvestmentScores.AsNoTracking().ToListAsync();
    foreach (var s in scores)
    {
        if (idMap.TryGetValue(s.ListingId, out var newId))
        {
            s.Id = 0;
            s.ListingId = newId;
            s.CalculatedAt = DateTime.SpecifyKind(s.CalculatedAt, DateTimeKind.Utc);
            await pgDb.InvestmentScores.AddAsync(s);
        }
    }
    
    // 4. Alerts
    var alerts = await sqliteDb.Alerts.AsNoTracking().ToListAsync();
    foreach (var a in alerts)
    {
        a.Id = 0;
        a.CreatedAt = DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc);
        if (a.LastTriggered.HasValue)
            a.LastTriggered = DateTime.SpecifyKind(a.LastTriggered.Value, DateTimeKind.Utc);
            
        await pgDb.Alerts.AddAsync(a);
    }
    
    await pgDb.SaveChangesAsync();

    return Results.Ok(new { 
        message = "Миграция завершена успешно",
        listings = oldListings.Count,
        scores = scores.Count,
        histories = histories.Count
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
            id = s.Listing!.Id,
            isInteresting = s.Listing.IsInteresting,
            lat = s.Listing.Latitude, lon = s.Listing.Longitude,
            title = s.Listing.Title.Substring(0, Math.Min(s.Listing.Title.Length, 60)),
            price = s.Listing.PriceUsd,
            pricePerSqm = Math.Round(s.Listing.PricePerSqm, 0),
            area = s.Listing.Area, rooms = s.Listing.Rooms,
            district = s.Listing.District,
            score = Math.Round(s.TotalScore, 1),
            recommendation = s.Recommendation, url = s.Listing.Url
        }).ToListAsync();

    // Фильтруем POI: метро — все, остальные — только именованные (убирает сотни безымянных мелких объектов)
    var poi = await db.PointsOfInterest
        .Where(p => p.Type == "metro" || (p.Name != "" && p.Name != "Парк" && p.Name != "Водоём" && p.Name != "Лес"))
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