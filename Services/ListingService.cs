using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public class ListingService
{
    private readonly AppDbContext _ctx;

    public ListingService(AppDbContext ctx) => _ctx = ctx;

    public async Task<int> SaveListingsAsync(List<Listing> listings)
    {
        int saved = 0;
        int updated = 0;
        var now = DateTime.UtcNow.ToString("o");

        foreach (var item in listings)
        {
            var existing = await _ctx.Listings.FirstOrDefaultAsync(l => l.ExternalId == item.ExternalId);
            if (existing == null)
            {
                // Новое объявление
                _ctx.Listings.Add(item);
                saved++;
            }
            else
            {
                // Существующее объявление - обновляем данные
                existing.ScrapedAt = item.ScrapedAt;
                
                // Проверяем, изменилась ли цена
                if (existing.PriceUsd != item.PriceUsd)
                {
                    existing.PriceUsd = item.PriceUsd;
                    existing.PricePerSqm = item.PricePerSqm;
                    
                    // Добавляем запись в историю цен
                    _ctx.PriceHistories.Add(new PriceHistory
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
            await _ctx.SaveChangesAsync();
            
            // Если были добавлены новые объявления, нам нужно получить их сгенерированные ID
            // для добавления первой записи в историю цен.
            if (saved > 0)
            {
                var newIds = listings.Select(l => l.ExternalId).ToList();
                var newlySavedListings = await _ctx.Listings
                    .Where(l => newIds.Contains(l.ExternalId))
                    .ToListAsync();

                foreach (var listing in newlySavedListings)
                {
                    // Проверяем, нет ли уже истории цен для этого объявления (чтобы не дублировать)
                    var hasHistory = await _ctx.PriceHistories.AnyAsync(p => p.ListingId == listing.Id);
                    if (!hasHistory)
                    {
                        _ctx.PriceHistories.Add(new PriceHistory
                        {
                            ListingId = listing.Id,
                            PricePerSqm = listing.PricePerSqm,
                            RecordedAt = now
                        });
                    }
                }
                await _ctx.SaveChangesAsync();
            }
        }

        return saved;
    }

    public async Task<PagedResult<Listing>> GetListingsAsync(ListingFilter? filter = null)
    {
        var query = _ctx.Listings.AsQueryable();

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
        }

        var totalCount = await query.CountAsync();
        var pageSize = filter?.PageSize ?? 20;
        var page = filter?.Page ?? 1;

        var items = await query
            .OrderByDescending(l => l.ScrapedAt)
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

    public async Task<MarketStats> GetStatsAsync()
    {
        var count = await _ctx.Listings.CountAsync();
        if (count == 0)
        {
            return new MarketStats();
        }

        var stats = new MarketStats
        {
            TotalListings = count,
            AvgPriceSqm = Math.Round(await _ctx.Listings.AverageAsync(l => l.PricePerSqm), 2),
            AvgPrice = Math.Round(await _ctx.Listings.AverageAsync(l => l.PriceUsd), 0)
        };

        stats.ByDistrict = await _ctx.Listings
            .GroupBy(l => l.District)
            .Select(g => new StatsItem { Key = g.Key, Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderByDescending(x => x.Count).ToListAsync();

        stats.ByType = await _ctx.Listings
            .GroupBy(l => l.FlatType)
            .Select(g => new StatsItem { Key = g.Key, Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderByDescending(x => x.Count).ToListAsync();

        stats.ByRooms = await _ctx.Listings
            .GroupBy(l => l.Rooms)
            .Select(g => new StatsItem { Key = g.Key.ToString(), Count = g.Count(), AvgPrice = Math.Round(g.Average(l => l.PricePerSqm), 2) })
            .OrderBy(x => x.Key).ToListAsync();

        return stats;
    }

    public async Task ToggleInterestingAsync(int id)
    {
        var listing = await _ctx.Listings.FindAsync(id);
        if (listing != null)
        {
            listing.IsInteresting = !listing.IsInteresting;
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetDistrictsAsync() =>
        await _ctx.Listings.Select(l => l.District).Distinct().Where(d => d != "Unknown").OrderBy(d => d).ToListAsync();

    public async Task<List<string>> GetFlatTypesAsync() =>
        await _ctx.Listings.Select(l => l.FlatType).Distinct().OrderBy(t => t).ToListAsync();
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
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
