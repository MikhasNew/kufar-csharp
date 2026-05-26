using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstateMinsk.Data;
using RealEstateMinsk.Services;

namespace RealEstateMinsk.Scratch;

public class CheckListing
{
    public static async Task Run(IServiceProvider sp)
    {
        Console.WriteLine("=== ЗАПУСК СКРИПТА ПРОВЕРКИ ОБЪЯВЛЕНИЯ ===");
        
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scraper = scope.ServiceProvider.GetRequiredService<KufarScraper>();

        var externalId = "1066887882";
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.ExternalId == externalId);

        if (listing == null)
        {
            Console.WriteLine($"[INFO] Объявление с ID {externalId} не найдено в базе данных.");
            return;
        }

        Console.WriteLine($"[DB] Найдено объявление: ID={listing.Id}, Title='{listing.Title}'");
        Console.WriteLine($"[DB] Статус IsClosed={listing.IsClosed}, ClosedAt={listing.ClosedAt}, ScrapedAt={listing.ScrapedAt}, CreatedAt={listing.CreatedAt}");
        Console.WriteLine($"[DB] URL: {listing.Url}");

        Console.WriteLine("[SCRAPER] Делаем HTTP-запрос для проверки активности на Kufar...");
        bool isActive = await scraper.IsListingActiveAsync(listing.Url);
        Console.WriteLine($"[SCRAPER] Результат проверки активности: {isActive} (true = активно, false = снято)");

        if (!isActive && !listing.IsClosed)
        {
            Console.WriteLine("[DB] Объявление снято на Kufar, но в базе помечено как активное. Исправляем...");
            listing.IsClosed = true;
            listing.ClosedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Console.WriteLine("[DB] Статус успешно обновлен в базе на IsClosed=True!");
        }
        else if (listing.IsClosed)
        {
            Console.WriteLine("[DB] Объявление уже корректно помечено в базе как закрытое.");
        }
        else
        {
            Console.WriteLine("[DB] Объявление по-прежнему активно на Kufar.");
        }

        Console.WriteLine("==========================================");
    }
}
