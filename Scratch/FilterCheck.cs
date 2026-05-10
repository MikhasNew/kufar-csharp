using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RealEstateMinsk.Scratch;

public class FilterCheck
{
    public static async Task Run(AppDbContext db)
    {
        var ghostItems = await db.Listings
            .Include(l => l.PriceHistories)
            .Where(l => l.PriceChangeUsd != 0)
            .ToListAsync();

        int missingHistoryCount = 0;
        int diffZeroCount = 0;
        
        foreach (var item in ghostItems)
        {
            var history = item.PriceHistories.Where(p => p.PriceUsd > 0).OrderBy(p => p.RecordedAt).ToList();
            if (history.Count <= 1)
            {
                missingHistoryCount++;
                // Console.WriteLine($"Item {item.Id} has PriceChangeUsd={item.PriceChangeUsd} but only {history.Count} history records.");
            }
            else
            {
                var firstPrice = history.First().PriceUsd;
                var diff = item.PriceUsd - firstPrice;
                if (diff == 0)
                {
                    diffZeroCount++;
                }
            }
        }

        Console.WriteLine($"Total items with PriceChangeUsd != 0: {ghostItems.Count}");
        Console.WriteLine($"Items that won't show badge because history count <= 1: {missingHistoryCount}");
        Console.WriteLine($"Items that won't show badge because diff == 0: {diffZeroCount}");
    }
}
