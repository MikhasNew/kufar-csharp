using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public class ListingService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public ListingService(IDbContextFactory<AppDbContext> contextFactory) => _contextFactory = contextFactory;

    /// <summary>
    /// Сохраняет список объявлений в БД. Возвращает tuple: (новые, обновлённые, все затронутые listing-и).
    /// </summary>
    public async Task<(int NewCount, int UpdatedCount, List<Listing> SavedListings)> SaveListingsAsync(List<Listing> listings)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        int saved = 0;
        int updated = 0;
        var now = DateTime.UtcNow.ToString("o");
        var savedListings = new List<Listing>();

        foreach (var item in listings)
        {
            var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.ExternalId == item.ExternalId);
            if (existing == null)
            {
                // Новое объявление
                ctx.Listings.Add(item);
                savedListings.Add(item); // EF Core сгенерирует Id при SaveChanges
                saved++;
            }
            else
            {
                // Существующее объявление - обновляем данные
                existing.ScrapedAt = item.ScrapedAt;

                // Проверяем, изменилась ли цена
                if (existing.PriceUsd != item.PriceUsd)
                {
                    // Мы должны найти ПЕРВУЮ зафиксированную цену, чтобы знать общий профит/убыток
                    var firstPrice = await ctx.PriceHistories
                        .Where(p => p.ListingId == existing.Id && p.PriceUsd > 0)
                        .OrderBy(p => p.RecordedAt)
                        .Select(p => p.PriceUsd)
                        .FirstOrDefaultAsync();

                    if (firstPrice == 0) firstPrice = existing.PriceUsd;

                    existing.PriceUsd = item.PriceUsd;
                    existing.PricePerSqm = item.PricePerSqm;
                    existing.PriceChangeUsd = item.PriceUsd - firstPrice;
                    
                    savedListings.Add(existing);

                    // Добавляем запись в историю цен
                    ctx.PriceHistories.Add(new PriceHistory
                    {
                        ListingId = existing.Id,
                        PriceUsd = item.PriceUsd,
                        PricePerSqm = item.PricePerSqm,
                        RecordedAt = now
                    });
                    updated++;
                }
            }
        }

        if (saved > 0 || updated > 0)
        {
            await ctx.SaveChangesAsync();

            // Если были добавлены новые объявления, нам нужно получить их сгенерированные ID
            // для добавления первой записи в историю цен.
            if (saved > 0)
            {
                var newIds = listings.Select(l => l.ExternalId).ToList();
                var newlySavedListings = await ctx.Listings
                    .Where(l => newIds.Contains(l.ExternalId))
                    .ToListAsync();

                foreach (var listing in newlySavedListings)
                {
                    // Проверяем, нет ли уже истории цен для этого объявления (чтобы не дублировать)
                    var hasHistory = await ctx.PriceHistories.AnyAsync(p => p.ListingId == listing.Id);
                    if (!hasHistory)
                    {
                        ctx.PriceHistories.Add(new PriceHistory
                        {
                            ListingId = listing.Id,
                            PriceUsd = listing.PriceUsd,
                            PricePerSqm = listing.PricePerSqm,
                            RecordedAt = now
                        });
                        listing.PriceChangeUsd = 0; // Для нового объявления изменение 0
                    }
                }
                await ctx.SaveChangesAsync();
            }
        }

        return (saved, updated, savedListings);
    }

    /// <summary>
    /// Инициализация поля PriceChangeUsd для существующих записей (разово)
    /// </summary>
    public async Task InitializePriceChangesAsync()
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var listings = await ctx.Listings.Include(l => l.PriceHistories).ToListAsync();
        foreach (var l in listings)
        {
            if (l.PriceHistories.Any())
            {
                var firstPrice = l.PriceHistories.OrderBy(p => p.RecordedAt).First(p => p.PriceUsd > 0).PriceUsd;
                l.PriceChangeUsd = l.PriceUsd - firstPrice;
            }
        }
        await ctx.SaveChangesAsync();
    }

    public async Task<PagedResult<Listing>> GetListingsAsync(ListingFilter? filter = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var query = ctx.Listings.Include(l => l.PriceHistories).AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.District))
                query = query.Where(l => l.District == filter.District);
            if (filter.Rooms > 0)
                query = query.Where(l => l.Rooms == filter.Rooms);
            if (!string.IsNullOrEmpty(filter.FlatType))
                query = query.Where(l => l.FlatType == filter.FlatType);
            if (filter.MinPrice > 0)
                query = query.Where(l => l.PriceUsd >= filter.MinPrice);
            if (filter.MaxPrice > 0)
                query = query.Where(l => l.PriceUsd <= filter.MaxPrice);
            if (filter.InterestingOnly)
                query = query.Where(l => l.IsInteresting);
            if (!string.IsNullOrEmpty(filter.Category) && filter.Category != "Все")
                query = query.Where(l => l.Category == filter.Category);
            if (filter.MaxDistanceToMinsk.HasValue)
                query = query.Where(l => l.DistanceToMinsk != null && l.DistanceToMinsk <= filter.MaxDistanceToMinsk);
            
            if (filter.PriceChangedOnly)
                query = query.Where(l => l.PriceChangeUsd != 0);

            // Сортировка
            query = filter.SortBy switch
            {
                "price_change_asc" => query.OrderBy(l => l.PriceChangeUsd),
                "price_change_desc" => query.OrderByDescending(l => l.PriceChangeUsd),
                "price_asc" => query.OrderBy(l => l.PriceUsd),
                "price_desc" => query.OrderByDescending(l => l.PriceUsd),
                _ => query.OrderByDescending(l => l.ScrapedAt)
            };
        }
        else
        {
            query = query.OrderByDescending(l => l.ScrapedAt);
        }

        var totalCount = await query.CountAsync();
        var pageSize = filter?.PageSize ?? 20;
        var page = filter?.Page ?? 1;

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<Listing>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<MarketStats> GetStatsAsync(string? category = null)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var query = ctx.Listings.AsQueryable();
        if (!string.IsNullOrEmpty(category) && category != "Все")
        {
            query = query.Where(l => l.Category == category);
        }

        var count = await query.CountAsync();
        if (count == 0)
        {
            return new MarketStats();
        }

        var stats = new MarketStats
        {
            TotalListings = count,
            AvgPriceSqm = Math.Round(await query.AverageAsync(l => l.PricePerSqm), 2),
            AvgPrice = Math.Round(await query.AverageAsync(l => l.PriceUsd), 0)
        };

        stats.ByDistrict = await query
            .GroupBy(l => l.District)
            .Select(g => new StatsItem { Key = g.Key, Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderByDescending(x => x.Count).ToListAsync();

        stats.ByType = await query
            .GroupBy(l => l.FlatType)
            .Select(g => new StatsItem { Key = g.Key, Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderByDescending(x => x.Count).ToListAsync();

        stats.ByRooms = await query
            .GroupBy(l => l.Rooms)
            .Select(g => new StatsItem { Key = g.Key.ToString(), Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderBy(x => x.Key).ToListAsync();

        return stats;
    }

    public async Task ToggleInterestingAsync(int id)
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        var listing = await ctx.Listings.FindAsync(id);
        if (listing != null)
        {
            listing.IsInteresting = !listing.IsInteresting;
            await ctx.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetDistrictsAsync()
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        return await ctx.Listings.Select(l => l.District).Distinct().Where(d => d != "Unknown").OrderBy(d => d).ToListAsync();
    }

    public async Task<List<string>> GetFlatTypesAsync()
    {
        using var ctx = await _contextFactory.CreateDbContextAsync();
        return await ctx.Listings.Select(l => l.FlatType).Distinct().OrderBy(t => t).ToListAsync();
    }
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class ListingFilter
{
    public string? District { get; set; }
    public int Rooms { get; set; }
    public string? FlatType { get; set; }
    public int MinPrice { get; set; }
    public int MaxPrice { get; set; }
    public bool InterestingOnly { get; set; }
    public string? Category { get; set; }
    public double? MaxDistanceToMinsk { get; set; }
    public bool PriceChangedOnly { get; set; }
    public string SortBy { get; set; } = "newest";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
