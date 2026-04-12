using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public interface IInvestmentAnalyzer
{
    Task<InvestmentScore> CalculateScoreAsync(Listing listing);
    Task<List<Listing>> GetTopInvestmentOpportunitiesAsync(int count = 20);
    Task<MarketTrend> GetMarketTrendAsync(string? district = null, int daysBack = 30);
    Task<PriceForecast> ForecastPriceAsync(Listing listing, int monthsAhead = 6);
    Task<List<Listing>> FindUndervaluedAsync(int thresholdPercent = 15);
    Task<ComparativeAnalysis> CompareDistrictsAsync();
    Task RecalculateAllScoresAsync();
}

public class InvestmentAnalyzer : IInvestmentAnalyzer
{
    private readonly AppDbContext _ctx;
    private readonly ILogger<InvestmentAnalyzer> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

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

    public InvestmentAnalyzer(AppDbContext ctx, ILogger<InvestmentAnalyzer> logger, IHttpClientFactory httpClientFactory)
    {
        _ctx = ctx;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Рассчитать инвестиционный скоринг для конкретного объявления
    /// </summary>
    public async Task<InvestmentScore> CalculateScoreAsync(Listing listing)
    {
        var score = new InvestmentScore
        {
            ListingId = listing.Id
        };

        // Авто-определение района, если его нет
        if (string.IsNullOrEmpty(listing.District) || listing.District == "Unknown")
        {
            if (listing.Latitude.HasValue && listing.Longitude.HasValue)
            {
                var (detected, isAuto) = await DetectDistrictAsync(listing.Latitude.Value, listing.Longitude.Value);
                if (isAuto)
                {
                    listing.District = detected;
                    listing.IsDistrictAutoDetected = true;
                    // Сохраняем обратно в БД для будущего использования
                    _ctx.Listings.Update(listing);
                    await _ctx.SaveChangesAsync();
                }
            }
        }

        // 1. Price Attractiveness (35%)
        (score.PriceAttractiveness, score.PriceRationale) = await CalculatePriceAttractivenessAsync(listing);

        // 2. Location Score (25%)
        (score.LocationScore, score.LocationRationale) = CalculateLocationScore(listing.District);

        // 3. Growth Potential (25%)
        (score.GrowthPotential, score.GrowthRationale) = await CalculateGrowthPotentialAsync(listing);

        // 4. Liquidity Score (15%)
        (score.LiquidityScore, score.LiquidityRationale) = CalculateLiquidityScore(listing);

        // Общий скоринг
        score.TotalScore = Math.Round(
            (score.PriceAttractiveness * PriceWeight) +
            (score.LocationScore * LocationWeight) +
            (score.GrowthPotential * GrowthWeight) +
            (score.LiquidityScore * LiquidityWeight),
            2
        );

        // Рекомендация
        (score.Recommendation, score.Rationale) = GenerateRecommendation(score, listing);

        score.CalculatedAt = DateTime.UtcNow;

        return score;
    }

    /// <summary>
    /// Получить топ инвестиционных возможностей
    /// </summary>
    public async Task<List<Listing>> GetTopInvestmentOpportunitiesAsync(int count = 20)
    {
        // Сначала пересчитаем скоринги для всех актуальных объявлений
        await RecalculateAllScoresAsync();

        // Получаем топ-N по общему скорингу с рекомендацией "Buy"
        var topScores = await _ctx.InvestmentScores
            .Include(s => s.Listing)
            .Where(s => s.Recommendation == "Buy")
            .OrderByDescending(s => s.TotalScore)
            .Take(count)
            .ToListAsync();

        // Если недостаточно "Buy", добавим лучшие "Hold"
        if (topScores.Count < count)
        {
            var remaining = count - topScores.Count;
            var topIds = topScores.Select(s => s.ListingId).ToHashSet();

            var holdScores = await _ctx.InvestmentScores
                .Include(s => s.Listing)
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
    public async Task<List<Listing>> FindUndervaluedAsync(int thresholdPercent = 15)
    {
        var listings = await _ctx.Listings.ToListAsync();
        var undervalued = new List<Listing>();

        foreach (var listing in listings)
        {
            var districtListings = await _ctx.Listings
                .Where(l => l.District == listing.District && l.District != "Unknown")
                .ToListAsync();

            if (!districtListings.Any()) continue;

            var districtAvg = districtListings.Average(l => l.PricePerSqm);

            if (districtAvg > 0)
            {
                var deviation = (districtAvg - listing.PricePerSqm) / districtAvg * 100;
                if (deviation >= thresholdPercent)
                {
                    undervalued.Add(listing);
                }
            }
        }

        return undervalued
            .OrderByDescending(l => l.PricePerSqm)
            .ToList();
    }

    /// <summary>
    /// Получить тренд рынка по району или всему городу
    /// </summary>
    public async Task<MarketTrend> GetMarketTrendAsync(string? district = null, int daysBack = 30)
    {
        var startDate = DateTime.UtcNow.AddDays(-daysBack);
        var startDateStr = startDate.ToString("o");

        // Фильтрация по району
        var allListings = _ctx.Listings.AsQueryable();
        if (!string.IsNullOrEmpty(district))
        {
            allListings = allListings.Where(l => l.District == district);
        }
        var listingIds = await allListings.Select(l => l.Id).ToListAsync();

        // Все записи истории цен для нужных объявлений
        var allHistory = await _ctx.PriceHistories
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
        var currentListings = await allListings.ToListAsync();
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
        var trend = await GetMarketTrendAsync(listing.District, 30);

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
    public async Task<ComparativeAnalysis> CompareDistrictsAsync()
    {
        var districts = await _ctx.Listings
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
            var trend = await GetMarketTrendAsync(d.District, 30);
            var avgScore = await _ctx.InvestmentScores
                .Where(s => s.Listing!.District == d.District)
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
    /// Пересчитать все инвестиционные скоринги
    /// </summary>
    public async Task RecalculateAllScoresAsync()
    {
        var listings = await _ctx.Listings.ToListAsync();
        var scores = new List<InvestmentScore>();

        foreach (var listing in listings)
        {
            try
            {
                var score = await CalculateScoreAsync(listing);
                scores.Add(score);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка расчета скоринга для listing {ListingId}", listing.Id);
            }
        }

        // Удаляем старые скоринги
        _ctx.InvestmentScores.RemoveRange(await _ctx.InvestmentScores.ToListAsync());

        // Добавляем новые
        await _ctx.InvestmentScores.AddRangeAsync(scores);
        await _ctx.SaveChangesAsync();

        _logger.LogInformation("Пересчитано {Count} инвестиционных скорингов", scores.Count);
    }

    // ==================== PRIVATE HELPER METHODS ====================

    /// <summary>
    /// Расчет привлекательности цены (0-100)
    /// Чем ниже цена относительно рынка, тем выше балл
    /// </summary>
    private async Task<(double Score, string Rationale)> CalculatePriceAttractivenessAsync(Listing listing)
    {
        const double RadiusKm = 1.0;
        const double AreaTolerancePercent = 0.20; // ±20% по площади считается "аналог"

        double radiusAvg = 0;
        int nearbyCount = 0;

        // === Гео-поиск аналогов в радиусе 1 км (если есть координаты) ===
        if (listing.Latitude.HasValue && listing.Longitude.HasValue)
        {
            var lat = listing.Latitude.Value;
            var lon = listing.Longitude.Value;

            // Bounding Box для быстрой предварительной фильтрации в SQLite
            var latDiff = RadiusKm / 111.0;
            var lonDiff = RadiusKm / (111.0 * Math.Cos(ToRadians(lat)));
            var minLat = lat - latDiff;
            var maxLat = lat + latDiff;
            var minLon = lon - lonDiff;
            var maxLon = lon + lonDiff;

            // Допустимый диапазон площади (±20%)
            var minArea = listing.Area * (1 - AreaTolerancePercent);
            var maxArea = listing.Area * (1 + AreaTolerancePercent);

            // Быстрый запрос к БД по "квадрату" + фильтр по метражу
            var candidates = await _ctx.Listings
                .Where(l => l.Id != listing.Id
                    && l.Latitude.HasValue && l.Longitude.HasValue
                    && l.Latitude >= minLat && l.Latitude <= maxLat
                    && l.Longitude >= minLon && l.Longitude <= maxLon
                    && l.Area >= minArea && l.Area <= maxArea)
                .ToListAsync();

            // Точный фильтр по расстоянию (Гаверсинус)
            var nearby = candidates
                .Where(c => CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value) <= RadiusKm)
                .ToList();

            nearbyCount = nearby.Count;

            if (nearbyCount >= 2) // Снижен порог до 2
            {
                // IDW (Взвешенная оценка по дистанции)
                double sumWeights = 0;
                double sumPriceWeights = 0;
                foreach (var c in nearby)
                {
                    var dist = CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value);
                    var weight = 1.0 / (dist + 0.01);
                    sumWeights += weight;
                    sumPriceWeights += c.PricePerSqm * weight;
                }
                radiusAvg = sumPriceWeights / sumWeights;
            }
        }

        // === Средняя цена/м² по району (фоллбэк) ===
        var districtListings = await _ctx.Listings
            .Where(l => l.District == listing.District && l.District != "Unknown")
            .ToListAsync();
        var districtAvg = districtListings.Any() ? districtListings.Average(l => l.PricePerSqm) : 0;

        // === Средняя цена/м² по типу квартиры ===
        var typeListings = await _ctx.Listings
            .Where(l => l.FlatType == listing.FlatType && l.FlatType != "Другой")
            .ToListAsync();
        var typeAvg = typeListings.Any() ? typeListings.Average(l => l.PricePerSqm) : 0;

        // === Общая средняя ===
        var allListings = await _ctx.Listings.ToListAsync();
        var overallAvg = allListings.Any() ? allListings.Average(l => l.PricePerSqm) : 0;

        if (radiusAvg == 0 && districtAvg == 0 && typeAvg == 0 && overallAvg == 0)
            return (50, "Нет данных для сравнения (нейтрально).");

        string rationale = "";
        double benchmark = 0;

        // Приоритет бенчмарка: 1) Радиус 1км → 2) Район → 3) Тип → 4) Общая
        if (radiusAvg > 0)
        {
            benchmark = radiusAvg;
            var minArea = Math.Round(listing.Area * 0.8, 1);
            var maxArea = Math.Round(listing.Area * 1.2, 1);
            rationale = $"Найдено {nearbyCount} аналогов в радиусе 1км (площадь {minArea}-{maxArea} м²). Метод: Взвешенная средняя (IDW): {Math.Round(benchmark, 0)} $/м².";
        }
        else if (nearbyCount > 0)
        {
            benchmark = districtAvg > 0 ? districtAvg : (typeAvg > 0 ? typeAvg : overallAvg);
            rationale = $"В радиусе 1км найдено лишь {nearbyCount} аналога (нужно 2 для IDW). Фоллбэк на " + 
                        (districtAvg > 0 ? $"район '{listing.District}'" : (typeAvg > 0 ? $"тип '{listing.FlatType}'" : "город")) + 
                        $": {Math.Round(benchmark, 0)} $/м².";
        }
        else if (districtAvg > 0)
        {
            benchmark = districtAvg;
            rationale = $"Средняя по району '{listing.District}': {Math.Round(benchmark, 0)} $/м².";
        }
        else if (typeAvg > 0)
        {
            benchmark = typeAvg;
            rationale = $"Средняя для типа '{listing.FlatType}': {Math.Round(benchmark, 0)} $/м².";
        }
        else
        {
            benchmark = overallAvg;
            rationale = $"Общая средняя по городу: {Math.Round(benchmark, 0)} $/м².";
        }

        var deviation = (benchmark - listing.PricePerSqm) / benchmark;
        var percent = Math.Round(deviation * 100, 1);
        
        rationale += (percent > 0 ? $" Объект дешевле на {percent}%." : $" Объект дороже на {Math.Abs(percent)}%.");
        
        if (listing.IsDistrictAutoDetected)
        {
            rationale += " (Район определен по координатам).";
        }

        rationale += $" [Обновлено: {DateTime.Now:dd.MM HH:mm}]";

        // Нормализация в 0-100
        var score = Math.Round((0.5 + deviation / 0.6) * 100, 2);
        return (Math.Clamp(score, 0, 100), rationale);
    }

    /// <summary>
    /// Расчет привлекательности локации (0-100)
    /// </summary>
    private (double Score, string Rationale) CalculateLocationScore(string district)
    {
        if (string.IsNullOrEmpty(district) || district == "Unknown")
            return (50, "Район не указан (нейтрально).");

        var rating = _districtRatings.GetValueOrDefault(district, 5.0);
        return (Math.Round(rating * 10, 2), $"Базовый рейтинг района '{district}': {rating}/10.");
    }

    /// <summary>
    /// Расчет потенциала роста (0-100)
    /// </summary>
    private async Task<(double Score, string Rationale)> CalculateGrowthPotentialAsync(Listing listing)
    {
        var trend = await GetMarketTrendAsync(listing.District, 30);
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
        var allListings = await _ctx.Listings.ToListAsync();
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

        if (score.LocationScore > 75)
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
    /// Определение района (Hybrid: DB Nearest 1km -> OSM API)
    /// </summary>
    private async Task<(string District, bool AutoDetected)> DetectDistrictAsync(double lat, double lon)
    {
        // 1. Поиск в БД в радиусе 1 км
        var latDiff = 1.0 / 111.0;
        var lonDiff = 1.0 / (111.0 * Math.Cos(ToRadians(lat)));
        var candidates = await _ctx.Listings
            .Where(l => l.Latitude.HasValue && l.Longitude.HasValue
                && l.Latitude >= lat - latDiff && l.Latitude <= lat + latDiff
                && l.Longitude >= lon - lonDiff && l.Longitude <= lon + lonDiff
                && l.District != "Unknown" && !string.IsNullOrEmpty(l.District))
            .ToListAsync();

        var nearest = candidates
            .Select(c => new { Dist = CalculateDistanceKm(lat, lon, c.Latitude!.Value, c.Longitude!.Value), c.District })
            .Where(c => c.Dist <= 1.0)
            .OrderBy(c => c.Dist)
            .FirstOrDefault();

        if (nearest != null)
        {
            return (nearest.District, true);
        }

        // 2. OSM Nominatim API Fallback
        try
        {
            var client = _httpClientFactory.CreateClient("Nominatim");
            var url = $"https://nominatim.openstreetmap.org/reverse?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&format=json&accept-language=ru";
            var response = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>(url);

            var districtNode = response?["address"]?["city_district"] ?? response?["address"]?["suburb"];
            if (districtNode != null)
            {
                var rawDistrict = districtNode.ToString();
                var mapping = new Dictionary<string, string> {
                    {"Центральны раён", "Центральный"}, {"Центральный район", "Центральный"},
                    {"Савецкі раён", "Советский"}, {"Советский район", "Советский"},
                    {"Першамайскі раён", "Первомайский"}, {"Первомайский район", "Первомайский"},
                    {"Партызанскі раён", "Партизанский"}, {"Партизанский район", "Партизанский"},
                    {"Заводскі раён", "Заводской"}, {"Заводской район", "Заводской"},
                    {"Ленінскі раён", "Ленинский"}, {"Ленинский район", "Ленинский"},
                    {"Кастрычніцкі раён", "Октябрьский"}, {"Октябрьский район", "Октябрьский"},
                    {"Маскоўскі раён", "Московский"}, {"Московский район", "Московский"},
                    {"Фрунзенскі раён", "Фрунзенский"}, {"Фрунзенский район", "Фрунзенский"}
                };

                if (mapping.TryGetValue(rawDistrict, out var matched)) return (matched, true);
                return (rawDistrict.Replace(" район", ""), true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка получения данных из OSM Nominatim");
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
