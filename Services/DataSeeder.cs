using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

/// <summary>
/// Сервис для ручного наполнения базы тестовыми данными
/// </summary>
public class DataSeeder
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<DataSeeder> _logger;

    public DataSeeder(IDbContextFactory<AppDbContext> contextFactory, ILogger<DataSeeder> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<int> SeedTestDataAsync()
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        ctx.Database.EnsureCreated();
        
        var existingCount = await ctx.Listings.CountAsync();
        if (existingCount > 50)
        {
            _logger.LogInformation("База уже содержит {Count} объявлений, пропускаю", existingCount);
            return 0;
        }

        _logger.LogInformation("Наполняю базу тестовыми данными...");

        var rand = new Random(42);
        var districts = new[] { "Центральный", "Советский", "Московский", "Фрунзенский", "Октябрьский", "Первомайский", "Ленинский", "Партизанский", "Заводской" };
        var flatTypes = new[] { "Новостройка", "Кирпичный", "Панельный", "Хрущевка", "Брежневка", "Каркасно-блочный" };
        var streets = new[] { "пр. Независимости", "ул. Ленина", "ул. Победителей", "пр. Пушкина", "ул. Мясникова", "ул. Калиновского", "ул. Сурганова", "ул. Богдановича", "ул. Притыцкого", "ул. Тимирязева" };

        var listings = new List<Listing>();

        for (int i = 0; i < 80; i++)
        {
            var district = districts[i % districts.Length];
            var rooms = (i % 3) + 1;
            var area = Math.Round(30 + rand.NextDouble() * 70, 2);

            var districtMultiplier = district switch
            {
                "Центральный" => 1.4,
                "Советский" => 1.2,
                "Первомайский" => 1.1,
                "Московский" => 1.15,
                _ => 1.0
            };

            var basePricePerSqm = 800 * districtMultiplier;
            var isUndervalued = i % 6 == 0;
            var pricePerSqm = isUndervalued 
                ? basePricePerSqm * (0.65 + rand.NextDouble() * 0.15) 
                : basePricePerSqm * (0.9 + rand.NextDouble() * 0.4);
            var price = (int)(pricePerSqm * area);

            listings.Add(new Listing
            {
                ExternalId = $"seed-{i:000}",
                Title = $"{rooms}-комн. квартира, {area}м², {streets[i % streets.Length]}",
                Description = isUndervalued 
                    ? "Срочная продажа! Возможна ипотека. Торг уместен." 
                    : "Хорошее состояние, рядом метро и инфраструктура.",
                PriceUsd = price,
                Area = area,
                PricePerSqm = Math.Round(pricePerSqm, 2),
                Rooms = rooms,
                District = district,
                FlatType = flatTypes[i % flatTypes.Length],
                Location = $"{streets[i % streets.Length]}, {district} район, Минск",
                Url = $"https://kufar.by/l/{i + 1000}",
                CreatedAt = DateTime.UtcNow.AddDays(-rand.Next(1, 30)),
                ScrapedAt = DateTime.UtcNow,
                IsInteresting = i % 8 == 0
            });
        }

        await ctx.Listings.AddRangeAsync(listings);
        await ctx.SaveChangesAsync();

        _logger.LogInformation("✅ Добавлено {Count} тестовых объявлений", listings.Count);
        return listings.Count;
    }
}
