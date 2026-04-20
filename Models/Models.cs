namespace RealEstateMinsk.Models;

public class Listing
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int PriceUsd { get; set; }
    public double Area { get; set; }
    public double PricePerSqm { get; set; }
    public int Rooms { get; set; }
    public string District { get; set; } = "";
    public string FlatType { get; set; } = "";
    public string Location { get; set; } = "";
    public string Url { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ScrapedAt { get; set; } = "";
    public bool IsInteresting { get; set; }
    public string? Notes { get; set; }
    public bool IsDistrictAutoDetected { get; set; } // [NEW] Флаг OSM/БД детекции
    public string Category { get; set; } = "Квартира";
    public double? LotSize { get; set; }
    public string? WallMaterial { get; set; }
    public double? DistanceToMinsk { get; set; }

    // Новые поля для аналитики
    public string? ImageUrl { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? YearBuilt { get; set; }
    public int? Floor { get; set; }
    public int? TotalFloors { get; set; }
}

public class PriceHistory
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int PriceUsd { get; set; }
    public double PricePerSqm { get; set; }
    public string RecordedAt { get; set; } = "";
}

public class MarketStats
{
    public int TotalListings { get; set; }
    public double AvgPriceSqm { get; set; }
    public double AvgPrice { get; set; }
    public List<StatsItem> ByDistrict { get; set; } = new();
    public List<StatsItem> ByType { get; set; } = new();
    public List<StatsItem> ByRooms { get; set; } = new();
}

public class StatsItem
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public double AvgPrice { get; set; }
}

// ==================== INVESTMENT ANALYSIS MODELS ====================

public class InvestmentScore
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing? Listing { get; set; }
    public double TotalScore { get; set; }          // 0-100 общий балл
    public double PriceAttractiveness { get; set; } // Насколько цена ниже рынка (0-100)
    public double LocationScore { get; set; }       // Привлекательность района (0-100)
    public double GrowthPotential { get; set; }     // Потенциал роста цены (0-100)
    public double LiquidityScore { get; set; }      // Ликвидность (0-100)
    public string Recommendation { get; set; } = "Hold"; // Buy / Hold / Avoid
    public string Rationale { get; set; } = "";     // Обоснование рекомендации
    
    // Детальное обоснование по компонентам
    public string PriceRationale { get; set; } = "";
    public string LocationRationale { get; set; } = "";
    public string GrowthRationale { get; set; } = "";
    public string LiquidityRationale { get; set; } = "";

    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
}

public class MarketTrend
{
    public string District { get; set; } = "";
    public double CurrentAvgPrice { get; set; }
    public double PreviousAvgPrice { get; set; }
    public double ChangePercent { get; set; }       // Процент изменения
    public string Trend { get; set; } = "";         // Growing / Stable / Declining
    public int SampleSize { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
}

public class PriceForecast
{
    public int ListingId { get; set; }
    public Listing? Listing { get; set; }
    public double CurrentPrice { get; set; }
    public double ForecastedPrice { get; set; }     // Прогноз цены
    public int MonthsAhead { get; set; }
    public double Confidence { get; set; }          // Уверенность прогноза (0-1)
    public string Trend { get; set; } = "";
    public DateTime ForecastDate { get; set; } = DateTime.UtcNow;
}

public class Alert
{
    public int Id { get; set; }
    public string Name { get; set; } = "";          // Название алерта
    public string? District { get; set; }           // Район (null = все)
    public int? MaxPrice { get; set; }              // Максимальная цена
    public int? MinPrice { get; set; }              // Минимальная цена
    public double? MaxPricePerSqm { get; set; }     // Макс цена за м²
    public int? MinRooms { get; set; }
    public int? MaxRooms { get; set; }
    public double MinScore { get; set; }            // Минимальный InvestmentScore
    public bool IsActive { get; set; } = true;
    public string? Email { get; set; }              // Email для уведомлений
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTriggered { get; set; }    // Последнее срабатывание
    public int TriggerCount { get; set; }           // Сколько раз сработал
}

public class ComparativeAnalysis
{
    public List<DistrictComparison> Districts { get; set; } = new();
    public string BestValueDistrict { get; set; } = "";
    public string FastestGrowingDistrict { get; set; } = "";
    public DateTime AnalysisDate { get; set; } = DateTime.UtcNow;
}

public class DistrictComparison
{
    public string Name { get; set; } = "";
    public double AvgPricePerSqm { get; set; }
    public int ListingCount { get; set; }
    public double AvgScore { get; set; }
    public double GrowthRate { get; set; }
    public double InvestmentIndex { get; set; }     // Композитный индекс привлекательности
}