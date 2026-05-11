using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RealEstateMinsk.Services;

namespace RealEstateMinsk.Scratch;

public class StatsCheck
{
    public static async Task Run(IServiceProvider sp)
    {
        var listingService = sp.GetRequiredService<ListingService>();
        try 
        {
            var stats = await listingService.GetStatsAsync();
            Console.WriteLine($"Stats loaded successfully: {stats.TotalListings} listings.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION IN GetStatsAsync: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
