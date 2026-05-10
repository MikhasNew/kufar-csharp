using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Threading;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public interface IInvestmentAnalyzer
{
    Task<InvestmentScore> CalculateScoreAsync(Listing listing);
    Task<InvestmentScore> GetOrCalculateScoreAsync(Listing listing, bool forceRecalculate = false);
    Task<InvestmentScore?> GetScoreForListingAsync(int listingId);
    Task<List<Listing>> GetTopInvestmentOpportunitiesAsync(int count = 20, string? category = null);
    Task<MarketTrend> GetMarketTrendAsync(string? district = null, int daysBack = 30, string? category = null);
    Task<PriceForecast> ForecastPriceAsync(Listing listing, int monthsAhead = 6);
    Task<List<Listing>> FindUndervaluedAsync(int thresholdPercent = 15, string? category = null);
    Task<ComparativeAnalysis> CompareDistrictsAsync(string? category = null);
    Task RecalculateAllScoresAsync();
    Task UpsertScoresAsync(IEnumerable<Listing> listings);
}

public class InvestmentAnalyzer : IInvestmentAnalyzer
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<InvestmentAnalyzer> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MinskGeoService _minskGeo;
    private readonly IConfiguration _config;

    // Веса для формулы скоринга
    private const double PriceWeight = 0.35;
    private const double LocationWeight = 0.25;
    private const double GrowthWeight = 0.25;
    private const double LiquidityWeight = 0.15;

    // Рейтинги районов Минска (1-10) - основаны на престижности, инфраструктуре, экологии
    private readonly Dictionary<string, double> _districtRatings = new()
    {
        ["Центральный"] = 9.0,
        ["Советский"] = 8.5,
        ["Первомайский"] = 8.0,
        ["Партизанский"] = 7.0,
        ["Заводской"] = 6.5,
        ["Ленинский"] = 7.0,
        ["Октябрьский"] = 7.5,
        ["Московский"] = 8.0,
        ["Фрунзенский"] = 7.5,
        ["Unknown"] = 5.0
    };

    private static readonly Dictionary<string, string> _osmDistrictMapping = new() {
        {"Центральный район", "Центральный"}, {"Центральны раён", "Центральный"},
        {"Советский район", "Советский"}, {"Савецкі раён", "Советский"},
        {"Первомайский район", "Первомайский"}, {"Першамайскі раён", "Первомайский"},
        {"Партизанский район", "Партизанский"}, {"Партызанскі раён", "Партизанский"},
        {"Заводской район", "Заводской"}, {"Заводскі раён", "Заводской"},
        {"Ленинский район", "Ленинский"}, {"Ленінскі раён", "Ленинский"},
        {"Октябрьский район", "Октябрьский"}, {"Кастрычніцкі раён", "Октябрьский"},
        {"Московский район", "Московский"}, {"Маскоўскі раён", "Московский"},
        {"Фрунзенский район", "Фрунзенский"}, {"Фрунзенскі раён", "Фрунзенский"}
    };

    private static readonly ConcurrentDictionary<string, string> _geoCache = new();
    private static readonly SemaphoreSlim _nominatimSemaphore = new(1, 1);

    /// <summary>
    /// Предзагруженный контекст для батчевого скоринга — загружается один раз для всей пачки.
    /// Устраняет N+1 запросы (раньше ~8 SQL/объявление, теперь 3 SQL на всю пачку).
    /// </summary>
    private sealed class ScoringContext
    {
        // Все активные объявления (c координатами и ценами)
        public List<Listing> AllListings { get; init; } = new();

        // Средняя цена/м² по категории
        public Dictionary<string, double> OverallAvgByCategory { get; init; } = new();

        // Средняя цена/м² по (категория, район)
        public Dictionary<(string Cat, string Dist), double> DistrictAvg { get; init; } = new();

        // Средняя цена/м² по (категория, тип)
        public Dictionary<(string Cat, string Type), double> TypeAvg { get; init; } = new();

        // История цен: ListingId → список записей (для тренда)
        public Dictionary<int, List<PriceHistory>> HistoryByListing { get; init; } = new();

        public List<PointOfInterest> MetroStations { get; init; } = new();
        public List<PointOfInterest> Parks { get; init; } = new();
        public List<PointOfInterest> WaterBodies { get; init; } = new();
        public List<PointOfInterest> Forests { get; init; } = new();

        public DateTime HistoryCutoff { get; init; }
    }

    public InvestmentAnalyzer(IDbContextFactory<AppDbContext> contextFactory, ILogger<InvestmentAnalyzer> logger, IHttpClientFactory httpClientFactory, MinskGeoService minskGeo, IConfiguration config)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _minskGeo = minskGeo;
        _config = config;
    }

    /// <summary>
    /// Выполняет 3 SQL-запроса и строит ScoringContext для батчевой обработки.
    /// </summary>
    private async Task<ScoringContext> BuildScoringContextAsync(AppDbContext ctx)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // 1 SQL: все объявления (id, lat, lon, area, pricePerSqm, district, flatType, category)
        var allListings = await ctx.Listings
            .Select(l => new Listing
            {
                Id = l.Id,
                Latitude = l.Latitude,
                Longitude = l.Longitude,
                Area = l.Area,
                PricePerSqm = l.PricePerSqm,
                PriceUsd = l.PriceUsd,
                District = l.District,
                FlatType = l.FlatType,
                Category = l.Category,
                DistanceToMinsk = l.DistanceToMinsk
            })
            .ToListAsync();

        // 2 SQL: вся история цен (только нужные поля)
        var allHistory = await ctx.PriceHistories
            .Select(h => new PriceHistory
            {
                ListingId = h.ListingId,
                PricePerSqm = h.PricePerSqm,
                RecordedAt = h.RecordedAt
            })
            .ToListAsync();

        var historyByListing = allHistory
            .GroupBy(h => h.ListingId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // In-memory агрегации
        var overallAvg = allListings
            .Where(l => l.PricePerSqm > 0)
            .GroupBy(l => l.Category)
            .ToDictionary(g => g.Key, g => g.Average(l => l.PricePerSqm));

        var districtAvg = allListings
            .Where(l => !string.IsNullOrEmpty(l.District) && l.District != "Unknown" && l.PricePerSqm > 0)
            .GroupBy(l => (l.Category, l.District))
            .ToDictionary(g => g.Key, g => g.Average(l => l.PricePerSqm));

        var typeAvg = allListings
            .Where(l => !string.IsNullOrEmpty(l.FlatType) && l.FlatType != "Другой" && l.PricePerSqm > 0)
            .GroupBy(l => (l.Category, l.FlatType))
            .ToDictionary(g => g.Key, g => g.Average(l => l.PricePerSqm));

        var allPoi = await ctx.PointsOfInterest
            .Select(p => new PointOfInterest { Latitude = p.Latitude, Longitude = p.Longitude, Name = p.Name, Type = p.Type })
            .ToListAsync();

        return new ScoringContext
        {
            AllListings = allListings,
            OverallAvgByCategory = overallAvg,
            DistrictAvg = districtAvg,
            TypeAvg = typeAvg,
            HistoryByListing = historyByListing,
            MetroStations = allPoi.Where(p => p.Type == "metro").ToList(),
            Parks = allPoi.Where(p => p.Type == "park").ToList(),
            WaterBodies = allPoi.Where(p => p.Type == "water").ToList(),
            Forests = allPoi.Where(p => p.Type == "forest").ToList(),
            HistoryCutoff = cutoff
        };
    }

    /// <summary>
    /// Рассчитать инвестиционный скоринг для одного объявления (для отображения на вкладке)
    /// </summary>
    public async Task<InvestmentScore> CalculateScoreAsync(Listing listing)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var scoringCtx = await BuildScoringContextAsync(ctx);
        var allowExternal = _config.GetValue<bool>("InvestmentAnalysis:AllowExternalGeoApi", true);
        return await CalculateScoreWithContextAsync(listing, scoringCtx, useExternalApi: allowExternal);
    }

    /// <summary>
    /// Внутренний метод скоринга — использует уже загруженный ScoringContext (0 доп. SQL-запросов)
    /// </summary>
    private async Task<InvestmentScore> CalculateScoreWithContextAsync(Listing listing, ScoringContext ctx, bool useExternalApi = true)
    {
        var score = new InvestmentScore
        {
            ListingId = listing.Id
        };

        // Авто-определение района, если его нет (Hybrid: GPS -> Address)
        if (string.IsNullOrEmpty(listing.District) || listing.District == "Unknown")
        {
            if (listing.Latitude.HasValue && listing.Longitude.HasValue)
            {
                var (detected, isAuto) = await DetectDistrictAsync(listing.Latitude.Value, listing.Longitude.Value, ctx, useExternalApi);
                if (isAuto && detected != "Unknown")
                {
                    listing.District = detected;
                    listing.IsDistrictAutoDetected = true;
                    _logger.LogInformation("Район для {ListingId} определен по GPS: {District}", listing.Id, detected);
                }
            }
            
            if ((string.IsNullOrEmpty(listing.District) || listing.District == "Unknown") && !string.IsNullOrEmpty(listing.Location))
            {
                var (detected, isAuto) = await DetectDistrictByAddressAsync(listing.Location, useExternalApi);
                if (isAuto && detected != "Unknown")
                {
                    listing.District = detected;
                    listing.IsDistrictAutoDetected = true;
                    _logger.LogInformation("Район для {ListingId} определен по адресу: {District}", listing.Id, detected);
                }
            }
        }

        var isHouse = listing.Category == "Дом" || listing.Category == "Дача";

        // 1. Price Attractiveness (35%) — in-memory, 0 SQL
        (score.PriceAttractiveness, score.PriceRationale) = CalculatePriceAttractivenessFromContext(listing, ctx);

        // 2. Location Score (25%)
        if (isHouse && listing.DistanceToMinsk.HasValue)
            (score.LocationScore, score.LocationRationale) = CalculateLocationScoreForHouse(listing.DistanceToMinsk.Value);
        else
            (score.LocationScore, score.LocationRationale) = CalculateLocationScore(listing.District);

        // === POI-бонусы ===
        if (listing.Latitude.HasValue && listing.Longitude.HasValue)
        {
            var lat = listing.Latitude.Value;
            var lon = listing.Longitude.Value;

            // 1. Метро (только для квартир)
            if (!isHouse && ctx.MetroStations.Count > 0)
            {
                var nearest = ctx.MetroStations
                    .Select(m => new { m.Name, Dist = CalculateDistanceKm(lat, lon, m.Latitude, m.Longitude) })
                    .OrderBy(m => m.Dist).First();

                if (nearest.Dist <= 0.8)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 15);
                    score.LiquidityScore = Math.Min(100, score.LiquidityScore + 10);
                    score.LocationRationale += $" +15 метро '{nearest.Name}' ({Math.Round(nearest.Dist * 1000)}м).";
                    score.LiquidityRationale += $" +10 (метро рядом).";
                }
                else if (nearest.Dist <= 1.5)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 8);
                    score.LocationRationale += $" +8 метро '{nearest.Name}' ({Math.Round(nearest.Dist, 1)}км).";
                }
            }

            // 2. Парки
            if (ctx.Parks.Count > 0)
            {
                var nearest = ctx.Parks
                    .Select(p => new { p.Name, Dist = CalculateDistanceKm(lat, lon, p.Latitude, p.Longitude) })
                    .OrderBy(p => p.Dist).First();

                if (nearest.Dist <= 0.5)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 8);
                    score.LiquidityScore = Math.Min(100, score.LiquidityScore + 5);
                    score.LocationRationale += $" +8 парк '{nearest.Name}' ({Math.Round(nearest.Dist * 1000)}м).";
                }
                else if (nearest.Dist <= 1.0)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 4);
                    score.LocationRationale += $" +4 парк '{nearest.Name}' ({Math.Round(nearest.Dist, 1)}км).";
                }
            }

            // 3. Водоёмы
            if (ctx.WaterBodies.Count > 0)
            {
                var nearest = ctx.WaterBodies
                    .Select(w => new { w.Name, Dist = CalculateDistanceKm(lat, lon, w.Latitude, w.Longitude) })
                    .OrderBy(w => w.Dist).First();

                if (nearest.Dist <= 0.3)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 10);
                    score.LiquidityScore = Math.Min(100, score.LiquidityScore + 5);
                    score.LocationRationale += $" +10 водоём '{nearest.Name}' ({Math.Round(nearest.Dist * 1000)}м).";
                }
                else if (nearest.Dist <= 0.8)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 5);
                    score.LocationRationale += $" +5 водоём '{nearest.Name}' ({Math.Round(nearest.Dist * 1000)}м).";
                }
            }

            // 4. Леса
            if (ctx.Forests.Count > 0)
            {
                var nearest = ctx.Forests
                    .Select(f => new { f.Name, Dist = CalculateDistanceKm(lat, lon, f.Latitude, f.Longitude) })
                    .OrderBy(f => f.Dist).First();

                if (nearest.Dist <= 0.5)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 6);
                    score.LocationRationale += $" +6 лес '{nearest.Name}' ({Math.Round(nearest.Dist * 1000)}м).";
                }
                else if (nearest.Dist <= 1.2)
                {
                    score.LocationScore = Math.Min(100, score.LocationScore + 3);
                    score.LocationRationale += $" +3 лес '{nearest.Name}' ({Math.Round(nearest.Dist, 1)}км).";
                }
            }
        }

        // 3. Growth Potential (25%) — in-memory, 0 SQL
        (score.GrowthPotential, score.GrowthRationale) = CalculateGrowthPotentialFromContext(listing, ctx);

        // 4. Liquidity Score (15%) — pure calculation, 0 SQL
        (score.LiquidityScore, score.LiquidityRationale) = CalculateLiquidityScore(listing);

        // Общий скоринг
        score.TotalScore = Math.Round(
            (score.PriceAttractiveness * PriceWeight) +
            (score.LocationScore * LocationWeight) +
            (score.GrowthPotential * GrowthWeight) +
            (score.LiquidityScore * LiquidityWeight),
            2
        );

        (score.Recommendation, score.Rationale) = GenerateRecommendation(score, listing);
        score.CalculatedAt = DateTime.UtcNow;

        return score;
    }

    /// <summary>
    /// Получить скоринг из БД или рассчитать новый, если он отсутствует или устарел (> 24ч)
    /// </summary>
    public async Task<InvestmentScore> GetOrCalculateScoreAsync(Listing listing, bool forceRecalculate = false)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        if (!forceRecalculate)
        {
            var cached = await ctx.InvestmentScores.FirstOrDefaultAsync(s => s.ListingId == listing.Id);
            if (cached != null && (DateTime.UtcNow - cached.CalculatedAt).TotalHours < 24)
            {
                return cached;
            }
        }

        var newScore = await CalculateScoreAsync(listing);
        await UpsertSingleScoreAsync(newScore);
        return newScore;
    }

    private async Task UpsertSingleScoreAsync(InvestmentScore score)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var existing = await ctx.InvestmentScores.FirstOrDefaultAsync(s => s.ListingId == score.ListingId);
        if (existing != null)
        {
            // Устанавливаем ID из существующей записи, чтобы SetValues не пытался его изменить
            score.Id = existing.Id;
            ctx.Entry(existing).CurrentValues.SetValues(score);
            existing.CalculatedAt = DateTime.UtcNow;
        }
        else
        {
            await ctx.InvestmentScores.AddAsync(score);
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Получить скоринг для конкретного объявления из БД (без пересчета)
    /// </summary>
    public async Task<InvestmentScore?> GetScoreForListingAsync(int listingId)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        return await ctx.InvestmentScores
            .FirstOrDefaultAsync(s => s.ListingId == listingId);
    }

    /// <summary>
    /// Получить топ инвестиционных возможностей
    /// </summary>
    public async Task<List<Listing>> GetTopInvestmentOpportunitiesAsync(int count = 20, string? category = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var query = ctx.InvestmentScores.Include(s => s.Listing).AsQueryable();
        if (!string.IsNullOrEmpty(category) && category != "Все")
        {
            query = query.Where(s => s.Listing!.Category == category);
        }

        var topScores = await query
            .Where(s => s.Recommendation == "Buy")
            .OrderByDescending(s => s.TotalScore)
            .Take(count)
            .ToListAsync();

        if (topScores.Count < count)
        {
            var remaining = count - topScores.Count;
            var topIds = topScores.Select(s => s.ListingId).ToHashSet();

            var holdScores = await query
                .Where(s => s.Recommendation == "Hold" && !topIds.Contains(s.ListingId))
                .OrderByDescending(s => s.TotalScore)
                .Take(remaining)
                .ToListAsync();

            topScores.AddRange(holdScores);
        }

        return topScores
            .Select(s => s.Listing!)
            .Where(l => l != null)
            .ToList()!;
    }

    /// <summary>
    /// Найти недооцененные объекты (цена ниже рынка на thresholdPercent%)
    /// </summary>
    public async Task<List<Listing>> FindUndervaluedAsync(int thresholdPercent = 15, string? category = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var query = ctx.Listings.AsQueryable();
        if (!string.IsNullOrEmpty(category) && category != "Все")
        {
            query = query.Where(l => l.Category == category);
        }

        var allListings = await query.ToListAsync();
        var districtAvgs = allListings
            .Where(l => l.District != "Unknown" && !string.IsNullOrEmpty(l.District))
            .GroupBy(l => l.District)
            .ToDictionary(g => g.Key, g => g.Average(l => l.PricePerSqm));

        var undervalued = new List<Listing>();

        foreach (var listing in allListings)
        {
            if (string.IsNullOrEmpty(listing.District) || listing.District == "Unknown") continue;
            
            if (!districtAvgs.TryGetValue(listing.District, out var districtAvg)) continue;

            var deviation = (districtAvg - listing.PricePerSqm) / districtAvg * 100;
            if (deviation >= thresholdPercent)
            {
                undervalued.Add(listing);
            }
        }

        return undervalued
            .OrderByDescending(l => l.PricePerSqm)
            .ToList();
    }

    /// <summary>
    /// Получить тренд рынка по району или всему городу
    /// </summary>
    public async Task<MarketTrend> GetMarketTrendAsync(string? district = null, int daysBack = 30, string? category = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var startDate = DateTime.UtcNow.AddDays(-daysBack);
        var startDateStr = startDate.ToString("o");

        var query = ctx.Listings.AsQueryable();
        if (!string.IsNullOrEmpty(district))
        {
            query = query.Where(l => l.District == district);
        }
        if (!string.IsNullOrEmpty(category) && category != "Все")
        {
            query = query.Where(l => l.Category == category);
        }
        var listingIds = await query.Select(l => l.Id).ToListAsync();

        // Все записи истории цен для нужных объявлений
        var allHistory = await ctx.PriceHistories
            .Where(h => listingIds.Contains(h.ListingId))
            .ToListAsync();

        // Разделяем на «старые» и «новые» записи
        var recentHistory = allHistory.Where(h =>
        {
            if (DateTime.TryParse(h.RecordedAt, out var parsed))
                return parsed >= startDate;
            return false;
        }).ToList();

        var olderHistory = allHistory.Where(h =>
        {
            if (DateTime.TryParse(h.RecordedAt, out var parsed))
                return parsed < startDate;
            return false;
        }).ToList();

        // Если нет исторических данных, используем текущие цены из Listings
        var currentListings = await query.ToListAsync();
        var currentAvg = currentListings.Any() ? currentListings.Average(l => l.PricePerSqm) : 0;
        var previousAvg = olderHistory.Any()
            ? olderHistory.Average(h => h.PricePerSqm)
            : currentAvg; // Если старых данных нет, тренд = 0%

        var trend = new MarketTrend
        {
            District = district ?? "All",
            CurrentAvgPrice = Math.Round(currentAvg, 2),
            PreviousAvgPrice = Math.Round(previousAvg, 2),
            SampleSize = currentListings.Count,
            PeriodStart = startDate,
            PeriodEnd = DateTime.UtcNow
        };

        if (trend.PreviousAvgPrice > 0)
        {
            trend.ChangePercent = Math.Round(
                (trend.CurrentAvgPrice - trend.PreviousAvgPrice) / trend.PreviousAvgPrice * 100, 2);

            trend.Trend = trend.ChangePercent switch
            {
                > 2 => "Growing",
                < -2 => "Declining",
                _ => "Stable"
            };
        }

        return trend;
    }

    /// <summary>
    /// Прогноз цены на основе тренда района
    /// </summary>
    public async Task<PriceForecast> ForecastPriceAsync(Listing listing, int monthsAhead = 6)
    {
        var trend = await GetMarketTrendAsync(listing.District, 30, listing.Category);

        var monthlyChange = trend.ChangePercent / 30 * 30; // Месячное изменение
        var projectedChange = monthlyChange * monthsAhead;
        var forecastedPricePerSqm = listing.PricePerSqm * (1 + projectedChange / 100);
        var forecastedPrice = forecastedPricePerSqm * listing.Area;

        // Уверенность падает с горизонтом прогноза
        var confidence = Math.Max(0.3, 1.0 - (monthsAhead * 0.05));

        return new PriceForecast
        {
            ListingId = listing.Id,
            CurrentPrice = listing.PriceUsd,
            ForecastedPrice = Math.Round(forecastedPrice, 0),
            MonthsAhead = monthsAhead,
            Confidence = Math.Round(confidence, 2),
            Trend = trend.Trend,
            ForecastDate = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Сравнительный анализ районов
    /// </summary>
    public async Task<ComparativeAnalysis> CompareDistrictsAsync(string? category = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var query = ctx.Listings.AsQueryable();
        if (!string.IsNullOrEmpty(category) && category != "Все")
        {
            query = query.Where(l => l.Category == category);
        }

        var districts = await query
            .Where(l => l.District != "Unknown")
            .GroupBy(l => l.District)
            .Select(g => new
            {
                District = g.Key,
                AvgPricePerSqm = g.Average(l => l.PricePerSqm),
                Count = g.Count()
            })
            .ToListAsync();

        var comparisons = new List<DistrictComparison>();

        foreach (var d in districts)
        {
            var trend = await GetMarketTrendAsync(d.District, 30, category);
            var avgScore = await ctx.InvestmentScores
                .Where(s => s.Listing!.District == d.District && s.Listing!.Category == category)
                .AverageAsync(s => (double?)s.TotalScore) ?? 0;

            // Investment Index = комбинация факторов
            var locationScore = CalculateLocationScore(d.District).Score / 100;
            var affordabilityIndex = 1 - (d.AvgPricePerSqm / (districts.Max(x => x.AvgPricePerSqm) * 1.5));
            var growthIndex = Math.Max(0, trend.ChangePercent + 10) / 20; // Нормализация

            var investmentIndex = (locationScore * 0.3) + (affordabilityIndex * 0.4) + (growthIndex * 0.3);

            comparisons.Add(new DistrictComparison
            {
                Name = d.District,
                AvgPricePerSqm = Math.Round(d.AvgPricePerSqm, 2),
                ListingCount = d.Count,
                AvgScore = Math.Round(avgScore, 2),
                GrowthRate = trend.ChangePercent,
                InvestmentIndex = Math.Round(investmentIndex * 100, 2)
            });
        }

        var analysis = new ComparativeAnalysis
        {
            Districts = comparisons.OrderByDescending(c => c.InvestmentIndex).ToList(),
            AnalysisDate = DateTime.UtcNow
        };

        if (analysis.Districts.Any())
        {
            analysis.BestValueDistrict = analysis.Districts.First().Name;
            analysis.FastestGrowingDistrict = analysis.Districts
                .OrderByDescending(d => d.GrowthRate)
                .First().Name;
        }

        return analysis;
    }

    /// <summary>
    /// Пересчитать все скоринги. Батчевая: все данные загружаются один раз.
    /// </summary>
    public async Task RecalculateAllScoresAsync()
    {
        _logger.LogInformation("Начало батчевого пересчёта скорингов...");
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var scoringCtx = await BuildScoringContextAsync(ctx);
        var listings = await ctx.Listings.ToListAsync();
        var scores = new List<InvestmentScore>(listings.Count);
        var modifiedListings = new List<Listing>();
        
        foreach (var listing in listings)
        {
            try
            {
                var allowExternal = _config.GetValue<bool>("InvestmentAnalysis:AllowExternalGeoApi", false);
                var isAutoBefore = listing.IsDistrictAutoDetected;
                var score = await CalculateScoreWithContextAsync(listing, scoringCtx, useExternalApi: allowExternal);
                scores.Add(score);
                
                if (listing.IsDistrictAutoDetected && !isAutoBefore)
                {
                    modifiedListings.Add(listing);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка расчета скоринга для listing {ListingId}", listing.Id);
            }
        }

        ctx.InvestmentScores.RemoveRange(await ctx.InvestmentScores.ToListAsync());
        await ctx.InvestmentScores.AddRangeAsync(scores);
        
        if (modifiedListings.Any())
        {
            ctx.Listings.UpdateRange(modifiedListings);
            _logger.LogInformation("Обновлено {Count} объявлений (авто-определение района)", modifiedListings.Count);
        }

        await ctx.SaveChangesAsync();

        _logger.LogInformation("Пересчитано {Count} скорингов (запросов к БД: 3 вместо {Old})", scores.Count, listings.Count * 8);
    }

    /// <summary>
    /// Рассчитать и сохранить/обновить скоринги только для переданных объявлений.
    /// Батчевая: все данные загружаются один раз + 1 SQL для удаления + 1 SQL для вставки.
    /// </summary>
    public async Task UpsertScoresAsync(IEnumerable<Listing> listings)
    {
        var listingList = listings.ToList();
        if (!listingList.Any()) return;

        using var ctx = await _contextFactory.CreateDbContextAsync();

        // Строим контекст один раз для всей пачки
        var scoringCtx = await BuildScoringContextAsync(ctx);

        var listingIds = listingList.Select(l => l.Id).ToHashSet();
        var scores = new List<InvestmentScore>(listingList.Count);
        var modifiedListings = new List<Listing>();
        int successCount = 0;
        int errorCount = 0;

        foreach (var listing in listingList)
        {
            if (listing.Id == 0)
            {
                _logger.LogWarning("Пропускаем listing без Id (ExternalId={ExternalId})", listing.ExternalId);
                errorCount++;
                continue;
            }

            try
            {
                var allowExternal = _config.GetValue<bool>("InvestmentAnalysis:AllowExternalGeoApi", false);
                var isAutoBefore = listing.IsDistrictAutoDetected;
                var score = await CalculateScoreWithContextAsync(listing, scoringCtx, useExternalApi: allowExternal);
                scores.Add(score);
                successCount++;

                if (listing.IsDistrictAutoDetected && !isAutoBefore)
                {
                    modifiedListings.Add(listing);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка скоринга для {ListingId}", listing.Id);
                errorCount++;
            }
        }

        if (scores.Count > 0)
        {
            var existingScores = await ctx.InvestmentScores
                .Where(s => listingIds.Contains(s.ListingId))
                .ToListAsync();
            ctx.InvestmentScores.RemoveRange(existingScores);
            await ctx.InvestmentScores.AddRangeAsync(scores);

            if (modifiedListings.Any())
            {
                ctx.Listings.UpdateRange(modifiedListings);
            }

            await ctx.SaveChangesAsync();
        }

        _logger.LogInformation("Батч-скоринг: {Success} OK / {Errors} ошибок, сохранено {Saved}",
            successCount, errorCount, scores.Count);
    }

    // ==================== PRIVATE HELPER METHODS ====================

    /// <summary>
    /// Расчёт привлекательности цены — полностью in-memory (0 SQL-запросов)
    /// </summary>
    private static (double Score, string Rationale) CalculatePriceAttractivenessFromContext(Listing listing, ScoringContext ctx)
    {
        var isHouse = listing.Category == "Дом" || listing.Category == "Дача";
        
        // Разные шкалы радиусов для квартир и домов
        var searchSteps = isHouse 
            ? new double[] { 5.0, 10.0, 20.0, 35.0, 50.0 }
            : new double[] { 1.0, 3.0, 5.0, 7.0, 10.0 };
            
        // Веса доверия для каждого шага
        var confidenceWeights = new double[] { 1.0, 0.85, 0.70, 0.55, 0.40 };

        const double AreaTolerance = 0.20;

        double benchmark = 0;
        int nearbyCount = 0;
        double usedRadius = 0;
        double usedConfidence = 1.0;
        string rationale = "";

        // Гео-поиск соседей in-memory с расширяющимся радиусом
        if (listing.Latitude.HasValue && listing.Longitude.HasValue)
        {
            var lat = listing.Latitude.Value;
            var lon = listing.Longitude.Value;
            var minArea = listing.Area * (1 - AreaTolerance);
            var maxArea = listing.Area * (1 + AreaTolerance);

            // Фильтруем кандидатов без учета расстояния (пока только по площади и категории)
            var candidates = ctx.AllListings
                .Where(l => l.Id != listing.Id
                    && l.Latitude.HasValue && l.Longitude.HasValue
                    && l.Area >= minArea && l.Area <= maxArea
                    && l.Category == listing.Category)
                .ToList();

            // Пробуем найти аналоги, постепенно увеличивая радиус
            for (int i = 0; i < searchSteps.Length; i++)
            {
                var currentRadius = searchSteps[i];
                var latDiff = currentRadius / 111.0;
                var lonDiff = currentRadius / (111.0 * Math.Cos(ToRadians(lat)));
                
                // Предварительная быстрая фильтрация по Bounding Box
                var bboxCandidates = candidates
                    .Where(c => c.Latitude >= lat - latDiff && c.Latitude <= lat + latDiff
                             && c.Longitude >= lon - lonDiff && c.Longitude <= lon + lonDiff);

                var nearby = bboxCandidates
                    .Where(c => CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value) <= currentRadius)
                    .ToList();

                if (nearby.Count >= 2)
                {
                    double sumW = 0, sumPW = 0;
                    foreach (var c in nearby)
                    {
                        var d = CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value);
                        var w = 1.0 / (d + 0.01);
                        sumW += w;
                        sumPW += c.PricePerSqm * w;
                    }
                    
                    benchmark = sumPW / sumW;
                    nearbyCount = nearby.Count;
                    usedRadius = currentRadius;
                    usedConfidence = confidenceWeights[i];
                    
                    var mn = Math.Round(minArea, 1);
                    var mx = Math.Round(maxArea, 1);
                    rationale = $"Найдено {nearbyCount} аналогов в {usedRadius} км ({mn}-{mx} м²), IDW: {Math.Round(benchmark, 0)} $/м².";
                    break; // Нашли достаточно аналогов, останавливаем поиск
                }
            }
        }

        // Фоллбэк-бенчмарки из предзагруженных словарей (если аналоги не найдены даже в максимальном радиусе)
        if (benchmark == 0)
        {
            ctx.DistrictAvg.TryGetValue((listing.Category, listing.District ?? ""), out var districtAvg);
            ctx.TypeAvg.TryGetValue((listing.Category, listing.FlatType ?? ""), out var typeAvg);
            ctx.OverallAvgByCategory.TryGetValue(listing.Category, out var overallAvg);

            if (districtAvg > 0)
            {
                benchmark = districtAvg;
                usedConfidence = 0.40; // Очень слабое доверие к среднему по району
                rationale = $"Аналогов в {searchSteps.Last()} км не найдено. Ср. по району '{listing.District}': {Math.Round(benchmark, 0)} $/м².";
            }
            else if (typeAvg > 0)
            {
                benchmark = typeAvg;
                usedConfidence = 0.30; // Ещё более слабое доверие к типу
                rationale = $"Аналогов нет. Фоллбэк на ср. для типа '{listing.FlatType}': {Math.Round(benchmark, 0)} $/м².";
            }
            else if (overallAvg > 0)
            {
                benchmark = overallAvg;
                usedConfidence = 0.20; // Минимальное доверие к средней по городу
                rationale = $"Аналогов нет. Фоллбэк на общую среднюю: {Math.Round(benchmark, 0)} $/м².";
            }
            else
            {
                return (50, "Нет данных для сравнения.");
            }
        }

        var deviation = (benchmark - listing.PricePerSqm) / benchmark;
        
        // Применяем вес уверенности (confidence). Если уверенность низкая, отклонение уменьшается
        var weightedDeviation = deviation * usedConfidence;
        
        var pct = Math.Round(deviation * 100, 1); // Показываем реальное отклонение в процентах
        rationale += pct > 0 ? $" Дешевле на {pct}%." : $" Дороже на {Math.Abs(pct)}%.";
        
        if (usedConfidence < 1.0)
        {
            rationale += $" (Доверие: {Math.Round(usedConfidence * 100)}%)";
        }

        if (listing.IsDistrictAutoDetected) rationale += " (район по GPS)";

        // Считаем итоговый скор на основе взвешенного отклонения
        var score = Math.Round((0.5 + weightedDeviation / 0.6) * 100, 2);
        return (Math.Clamp(score, 0, 100), rationale);
    }

    /// <summary>
    /// Расчёт потенциала роста — in-memory (0 SQL-запросов)
    /// </summary>
    private static (double Score, string Rationale) CalculateGrowthPotentialFromContext(Listing listing, ScoringContext ctx)
    {
        var isHouse = listing.Category == "Дом" || listing.Category == "Дача";
        var rationale = new List<string>();

        var cutoff = ctx.HistoryCutoff;
        var districtListingIds = ctx.AllListings
            .Where(l => l.District == listing.District && l.Category == listing.Category)
            .Select(l => l.Id)
            .ToHashSet();

        var oldPrices = ctx.HistoryByListing
            .Where(kv => districtListingIds.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .Where(h => DateTime.TryParse(h.RecordedAt, out var d) && d < cutoff)
            .Select(h => h.PricePerSqm)
            .ToList();

        var curAvg = ctx.AllListings
            .Where(l => l.District == listing.District && l.Category == listing.Category && l.PricePerSqm > 0)
            .Select(l => l.PricePerSqm)
            .DefaultIfEmpty(0).Average();

        double score;
        if (oldPrices.Count >= 3)
        {
            var prevAvg = oldPrices.Average();
            var change = prevAvg > 0 ? (curAvg - prevAvg) / prevAvg * 100 : 0;
            score = change switch { > 2 => 70 + Math.Min(30, change * 5), < -2 => Math.Max(20, 50 + change * 5), _ => 50 };
            rationale.Add($"Тренд района: {Math.Round(change, 1)}% за 30 дней.");
        }
        else
        {
            score = 50;
            rationale.Add("Мало исторических данных для тренда.");
        }

        if (isHouse && listing.DistanceToMinsk.HasValue)
        {
            var dist = listing.DistanceToMinsk.Value;
            if (dist <= 20)  { score += 15; rationale.Add($"+15 ({dist:N0} км — высокий спрос на пригород)."); }
            else if (dist <= 30) { score += 10; rationale.Add($"+10 ({dist:N0} км — субурбанизация)."); }
            else if (dist > 120) { score -= 25; rationale.Add($"-25 ({dist:N0} км — очень низкий спрос)."); }
            else if (dist > 80)  { score -= 15; rationale.Add($"-15 ({dist:N0} км — удалённый объект)."); }
        }

        if (ctx.OverallAvgByCategory.TryGetValue(listing.Category, out var overallAvg) && overallAvg > 0
            && listing.PricePerSqm < overallAvg * 0.85)
        {
            score += 10;
            rationale.Add("+10 (цена ниже рынка).");
        }

        return (Math.Round(Math.Clamp(score, 0, 100), 2), string.Join(" ", rationale));
    }

    /// <summary>
    /// Расчет привлекательности локации по РЕЙТИНГУ РАЙОНА (для квартир)
    /// </summary>
    private (double Score, string Rationale) CalculateLocationScore(string district)
    {
        if (string.IsNullOrEmpty(district) || district == "Unknown" || district.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return (50, "Район не указан (нейтрально).");

        var d = district.Trim();
        var key = _districtRatings.Keys.FirstOrDefault(k => k.Equals(d, StringComparison.OrdinalIgnoreCase));
        
        if (key != null)
        {
            var rating = _districtRatings[key];
            return (Math.Round(rating * 10, 2), $"Рейтинг района '{key}': {rating}/10.");
        }

        _logger.LogWarning("Район '{District}' не найден в справочнике рейтингов", d);
        return (50, $"Район '{d}' не найден в справочнике.");
    }

    /// <summary>
    /// Расчет привлекательности локации по РАССТОЯНИЮ ДО МИНСКА (для домов)
    /// Районный рейтинг для пригородных домов бессмысленен — важна дистанция.
    /// </summary>
    private static (double Score, string Rationale) CalculateLocationScoreForHouse(double distanceKm)
    {
        double score;
        string label;

        if (distanceKm <= 10)
        {
            score = 90 + (10 - distanceKm); // 90–100
            label = "фактически в черте Минска (МКАД)";
        }
        else if (distanceKm <= 20)
        {
            score = 75 + (20 - distanceKm) * 1.5; // 75–90
            label = "ближний пригород";
        }
        else if (distanceKm <= 35)
        {
            score = 55 + (35 - distanceKm) * 1.33; // 55–75
            label = "популярный пригород";
        }
        else if (distanceKm <= 60)
        {
            score = 35 + (60 - distanceKm) * 0.8; // 35–55
            label = "далёкий пригород";
        }
        else if (distanceKm <= 100)
        {
            score = 20 + (100 - distanceKm) * 0.375; // 20–35
            label = "районный центр";
        }
        else
        {
            score = Math.Max(0, 20 - (distanceKm - 100) * 0.2); // 0–20
            label = "глубокая провинция";
        }

        return (Math.Round(Math.Clamp(score, 0, 100), 1),
            $"{Math.Round(distanceKm, 1)} км от Минска — {label}.");
    }

    /// <summary>
    /// Расчет потенциала роста (0-100)
    /// </summary>
    private async Task<(double Score, string Rationale)> CalculateGrowthPotentialAsync(Listing listing)
    {
        var trend = await GetMarketTrendAsync(listing.District, 30, listing.Category);
        List<string> rationale = new() { $"Тренд: {trend.Trend} за 30 дней ({trend.ChangePercent}%)." };

        // Базовый скоринг на основе тренда
        double score;
        if (trend.Trend == "Growing")
        {
            score = 70 + Math.Min(30, trend.ChangePercent * 5);
        }
        else if (trend.Trend == "Declining")
        {
            score = Math.Max(20, 50 + trend.ChangePercent * 5);
        }
        else
        {
            score = 50;
        }

        // Бонус для недооцененных районов (ниже среднего)
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var allListings = await ctx.Listings.Where(l => l.Category == listing.Category).ToListAsync();
        if (allListings.Any())
        {
            var overallAvg = allListings.Average(l => l.PricePerSqm);
            if (listing.PricePerSqm < overallAvg * 0.85)
            {
                score += 10; // Бонус за потенциал
                rationale.Add("Бонус +10 за недооценку района.");
            }
        }

        return (Math.Round(Math.Clamp(score, 0, 100), 2), string.Join(" ", rationale));
    }

    /// <summary>
    /// Расчет ликвидности (0-100)
    /// </summary>
    private (double Score, string Rationale) CalculateLiquidityScore(Listing listing)
    {
        double score = 50; // Базовый
        var rationale = new List<string> { "База (50)" };
        var isHouse = listing.Category == "Дом" || listing.Category == "Дача";

        if (isHouse)
        {
            // --- Правила ликвидности для ДОМОВ ---

            // Размер дома (комнаты)
            if (listing.Rooms >= 3 && listing.Rooms <= 5)
            {
                score += 15; rationale.Add("+15 (3-5 комн.)");
            }
            else if (listing.Rooms > 5)
            {
                score -= 5; rationale.Add("-5 (>5 комн.)");
            }

            // Ценовой сегмент
            if (listing.PriceUsd <= 50000)
            {
                score += 15; rationale.Add("+15 (<$50k)");
            }
            else if (listing.PriceUsd <= 100000)
            {
                score += 10; rationale.Add("+10 (<$100k)");
            }
            else if (listing.PriceUsd > 200000)
            {
                score -= 10; rationale.Add("-10 (>$200k)");
            }

            // Год постройки
            if (listing.YearBuilt.HasValue)
            {
                if (listing.YearBuilt < 1990)
                {
                    score -= 10; rationale.Add("-10 (До 1990)");
                }
                else if (listing.YearBuilt >= 2015)
                {
                    score += 15; rationale.Add("+15 (Дом >=2015)");
                }
                else if (listing.YearBuilt >= 2005)
                {
                    score += 5; rationale.Add("+5 (Дом >=2005)");
                }
            }

            // Размер участка
            if (listing.LotSize.HasValue)
            {
                if (listing.LotSize > 15)
                {
                    score += 5; rationale.Add("+5 (Участок >15 сот.)");
                }
                else if (listing.LotSize >= 6)
                {
                    score += 15; rationale.Add("+15 (Участок 6-15 сот.)");
                }
                else if (listing.LotSize < 4)
                {
                    score -= 5; rationale.Add("-5 (Участок <4 сот.)");
                }
            }

            // Расстояние до Минска (ликвидность: ближе — быстрее продать)
            if (listing.DistanceToMinsk.HasValue)
            {
                var dist = listing.DistanceToMinsk.Value;
                if (dist <= 20)
                {
                    score += 10; rationale.Add($"+10 ({dist:N0} км от Минска — высокая ликвидность)");
                }
                else if (dist <= 35)
                {
                    score += 5; rationale.Add($"+5 ({dist:N0} км от Минска)");
                }
                else if (dist > 120)
                {
                    score -= 15; rationale.Add($"-15 ({dist:N0} км от Минска — очень низкая ликвидность)");
                }
                else if (dist > 80)
                {
                    score -= 10; rationale.Add($"-10 ({dist:N0} км от Минска)");
                }
            }

            // Материал стен
            if (!string.IsNullOrEmpty(listing.WallMaterial))
            {
                if (listing.WallMaterial.Contains("кирпич", StringComparison.OrdinalIgnoreCase) || 
                    listing.WallMaterial.Contains("блок", StringComparison.OrdinalIgnoreCase))
                {
                    score += 10; rationale.Add("+10 (Кирпич/Блок)");
                }
                else if (listing.WallMaterial.Contains("дерев", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 5; rationale.Add("-5 (Дерево)");
                }
            }
        }
        else
        {
            // --- Правила ликвидности для КВАРТИР ---

            // 1-2 комнатные более ликвидные
            if (listing.Rooms == 1 || listing.Rooms == 2)
            {
                score += 20; rationale.Add("+20 (1-2 комн.)");
            }
            else if (listing.Rooms == 3)
            {
                score += 10; rationale.Add("+10 (3 комн.)");
            }

            // Ценовой сегмент (до $50k - высокая ликвидность)
            if (listing.PriceUsd <= 50000)
            {
                score += 15; rationale.Add("+15 (<$50k)");
            }
            else if (listing.PriceUsd <= 80000)
            {
                score += 10; rationale.Add("+10 (<$80k)");
            }
            else if (listing.PriceUsd > 150000)
            {
                score -= 10; rationale.Add("-10 (>$150k)");
            }

            // Тип дома
            if (listing.FlatType == "Новостройка")
            {
                score += 10; rationale.Add("+10 (Новостройка)");
            }
            else if (listing.FlatType == "Кирпичный")
            {
                score += 5; rationale.Add("+5 (Кирпичный)");
            }

            // Этажность: штраф за 1-й и последний этаж
            if (listing.Floor.HasValue)
            {
                if (listing.Floor == 1)
                {
                    score -= 10; rationale.Add("-10 (Первый этаж)");
                }
                else if (listing.TotalFloors.HasValue && listing.Floor == listing.TotalFloors)
                {
                    score -= 10; rationale.Add("-10 (Последний этаж)");
                }
            }

            // Год постройки
            if (listing.YearBuilt.HasValue)
            {
                if (listing.YearBuilt < 1975)
                {
                    score -= 10; rationale.Add("-10 (До 1975)");
                }
                else if (listing.YearBuilt >= 2015)
                {
                    score += 15; rationale.Add("+15 (Дом >=2015)");
                }
                else if (listing.YearBuilt >= 2000)
                {
                    score += 5; rationale.Add("+5 (Дом >=2000)");
                }
            }
        }

        return (Math.Clamp(score, 0, 100), string.Join(", ", rationale) + ".");
    }

    /// <summary>
    /// Генерация рекомендации с обоснованием
    /// </summary>
    private (string Recommendation, string Rationale) GenerateRecommendation(InvestmentScore score, Listing listing)
    {
        var reasons = new List<string>();

        if (score.PriceAttractiveness > 70)
            reasons.Add($"Цена на {Math.Round(100 - score.PriceAttractiveness)}% ниже рынка");

        if (score.LocationScore >= 75)
            reasons.Add("Отличная локация");

        if (score.GrowthPotential > 70)
            reasons.Add("Высокий потенциал роста");

        if (score.LiquidityScore > 70)
            reasons.Add("Высокая ликвидность");

        if (score.PriceAttractiveness < 30)
            reasons.Add("Цена выше рынка");

        if (score.GrowthPotential < 40)
            reasons.Add("Снижающийся тренд в районе");

        string recommendation;
        if (score.TotalScore >= 70)
        {
            recommendation = "Buy";
        }
        else if (score.TotalScore >= 50)
        {
            recommendation = "Hold";
        }
        else
        {
            recommendation = "Avoid";
        }

        var rationale = reasons.Any()
            ? string.Join(". ", reasons)
            : "Недостаточно данных для детальной оценки";

        return (recommendation, rationale);
    }

    // ==================== GEO HELPER METHODS ====================

    /// <summary>
    /// Определение района (Hybrid: Local Polygon -> DB Nearest 1km -> OSM API)
    /// </summary>
    private async Task<(string District, bool AutoDetected)> DetectDistrictAsync(double lat, double lon, ScoringContext ctx, bool useExternalApi = true)
    {
        // 1. Локальный геокодер (мгновенно, без HTTP)
        var localDistrict = _minskGeo.GetDistrictByCoords(lat, lon);
        if (localDistrict != null)
        {
            _logger.LogDebug("Район определён локально: {District}", localDistrict);
            return (localDistrict, true);
        }

        // 2. Поиск в памяти в радиусе 1 км (фоллбэк для пригородов)
        var latDiff = 1.0 / 111.0;
        var lonDiff = 1.0 / (111.0 * Math.Cos(ToRadians(lat)));
        var candidates = ctx.AllListings
            .Where(l => l.Latitude.HasValue && l.Longitude.HasValue
                && l.Latitude >= lat - latDiff && l.Latitude <= lat + latDiff
                && l.Longitude >= lon - lonDiff && l.Longitude <= lon + lonDiff
                && l.District != "Unknown" && !string.IsNullOrEmpty(l.District))
            .ToList();

        var nearest = candidates
            .Select(c => new { Dist = CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value), c.District })
            .Where(c => c.Dist <= 1.0)
            .OrderBy(c => c.Dist)
            .FirstOrDefault();

        if (nearest != null)
        {
            return (nearest.District, true);
        }

        if (!useExternalApi)
        {
            return ("Unknown", false);
        }

        // 3. OSM Nominatim API Fallback (последний resort)
        var cacheKey = $"coords_{Math.Round(lat, 4)}_{Math.Round(lon, 4)}";
        if (_geoCache.TryGetValue(cacheKey, out var cached))
        {
            return (cached, true);
        }

        try
        {
            await _nominatimSemaphore.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient("Nominatim");
                var url = $"https://nominatim.openstreetmap.org/reverse?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&format=json&accept-language=ru";
                var response = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>(url);

                var districtNode = response?["address"]?["city_district"] ?? response?["address"]?["suburb"];
                if (districtNode != null)
                {
                    var rawDistrict = districtNode.ToString().Trim();
                    string detected;
                    if (_osmDistrictMapping.TryGetValue(rawDistrict, out var matched))
                        detected = matched;
                    else
                        detected = rawDistrict.Replace(" район", "").Replace(" раён", "").Trim();

                    _geoCache.TryAdd(cacheKey, detected);
                    return (detected, true);
                }
            }
            finally
            {
                await Task.Delay(1000); // Respect OSM 1 req/sec limit
                _nominatimSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка обратного геокодинга через OSM Nominatim");
        }

        return ("Unknown", false);
    }

    /// <summary>
    /// Определение района по адресу (Hybrid: Local Microdistrict Registry -> OSM Geocoding)
    /// </summary>
    private async Task<(string District, bool IsAuto)> DetectDistrictByAddressAsync(string address, bool useExternalApi = true)
    {
        // 1. Локальный поиск по микрорайонам (мгновенно, без HTTP)
        var localDistrict = _minskGeo.GetDistrictByAddress(address);
        if (localDistrict != null)
        {
            _logger.LogDebug("Район определён по микрорайону: {District}", localDistrict);
            return (localDistrict, true);
        }

        if (!useExternalApi)
        {
            return ("Unknown", false);
        }

        // 2. OSM Nominatim API Fallback
        var cacheKey = $"addr_{address.ToLowerInvariant()}";
        if (_geoCache.TryGetValue(cacheKey, out var cached))
        {
            return (cached, true);
        }

        try
        {
            await _nominatimSemaphore.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient("Nominatim");
                // Добавляем "Минск" к запросу для точности
                var query = Uri.EscapeDataString($"{address}, Минск");
                var url = $"https://nominatim.openstreetmap.org/search?q={query}&format=json&addressdetails=1&limit=1&accept-language=ru";

                var results = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>(url);
                if (results != null && results.Count > 0)
                {
                    var addressNode = results[0]?["address"];
                    var districtNode = addressNode?["city_district"] ?? addressNode?["suburb"];

                    if (districtNode != null)
                    {
                        var rawDistrict = districtNode.ToString().Trim();
                        string detected;
                        if (_osmDistrictMapping.TryGetValue(rawDistrict, out var matched))
                            detected = matched;
                        else
                            detected = rawDistrict.Replace(" район", "").Replace(" раён", "").Trim();

                        _geoCache.TryAdd(cacheKey, detected);
                        return (detected, true);
                    }
                }
            }
            finally
            {
                await Task.Delay(1000); // Respect OSM 1 req/sec limit
                _nominatimSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка прямого геокодинга через OSM Nominatim для адреса: {Address}", address);
        }

        return ("Unknown", false);
    }

    /// <summary>
    /// Расстояние между двумя GPS-точками в километрах (формула Гаверсинуса)
    /// </summary>
    private static double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0; // Радиус Земли в км
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
