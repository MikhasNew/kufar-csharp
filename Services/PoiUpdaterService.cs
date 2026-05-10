using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using RealEstateMinsk.Data;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public class PoiUpdaterService
{
    private readonly HttpClient _http;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PoiUpdaterService> _logger;
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";

    public PoiUpdaterService(HttpClient http, IServiceProvider serviceProvider, ILogger<PoiUpdaterService> logger)
    {
        _http = http;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Обновляет все типы POI: метро, парки, водоёмы, леса.
    /// </summary>
    public async Task UpdateAllPoiAsync()
    {
        await UpdateMetroStationsAsync();
        await Task.Delay(2000); // Пауза между запросами к Overpass
        await UpdateParksAsync();
        await Task.Delay(2000);
        await UpdateWaterBodiesAsync();
        await Task.Delay(2000);
        await UpdateForestsAsync();
    }

    public async Task UpdateMetroStationsAsync()
    {
        _logger.LogInformation("Загрузка станций метро Минска...");
        var query = @"
            [out:json];
            area[""name:ru""=""Минск""][""admin_level""=""4""]->.city;
            node(area.city)[""station""=""subway""];
            out body;";

        var elements = await ExecuteOverpassQuery(query);
        if (elements == null) return;

        var pois = new List<PointOfInterest>();
        foreach (var el in elements.Value.EnumerateArray())
        {
            if (el.GetProperty("type").GetString() != "node") continue;
            var lat = el.GetProperty("lat").GetDouble();
            var lon = el.GetProperty("lon").GetDouble();
            var tags = el.GetProperty("tags");
            var name = GetTag(tags, "name:ru") ?? GetTag(tags, "name") ?? "Станция метро";
            var line = GetTag(tags, "colour") ?? GetTag(tags, "network:ru");

            pois.Add(new PointOfInterest
            {
                Name = name, Type = "metro",
                Latitude = lat, Longitude = lon,
                GeoLocation = new Point(lon, lat) { SRID = 4326 },
                Extra = line, UpdatedAt = DateTime.UtcNow
            });
        }
        await SavePois("metro", pois);
    }

    public async Task UpdateParksAsync()
    {
        _logger.LogInformation("Загрузка парков Минска...");
        // Парки площадью > ~1 га в пределах Минска и ближнего пригорода
        var query = @"
            [out:json];
            (
              way(53.82,27.35,54.00,27.75)[""leisure""=""park""];
              relation(53.82,27.35,54.00,27.75)[""leisure""=""park""];
            );
            out center;";

        var elements = await ExecuteOverpassQuery(query);
        if (elements == null) return;

        var pois = new List<PointOfInterest>();
        foreach (var el in elements.Value.EnumerateArray())
        {
            if (!el.TryGetProperty("center", out var center) &&
                !el.TryGetProperty("lat", out _)) continue;

            double lat, lon;
            if (el.TryGetProperty("center", out var c))
            {
                lat = c.GetProperty("lat").GetDouble();
                lon = c.GetProperty("lon").GetDouble();
            }
            else
            {
                lat = el.GetProperty("lat").GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }

            var tags = el.TryGetProperty("tags", out var t) ? t : default;
            var name = GetTag(tags, "name:ru") ?? GetTag(tags, "name") ?? "Парк";

            pois.Add(new PointOfInterest
            {
                Name = name, Type = "park",
                Latitude = lat, Longitude = lon,
                GeoLocation = new Point(lon, lat) { SRID = 4326 },
                UpdatedAt = DateTime.UtcNow
            });
        }
        await SavePois("park", pois);
    }

    public async Task UpdateWaterBodiesAsync()
    {
        _logger.LogInformation("Загрузка водоёмов Минска...");
        var query = @"
            [out:json];
            (
              way(53.82,27.35,54.00,27.75)[""natural""=""water""];
              relation(53.82,27.35,54.00,27.75)[""natural""=""water""];
            );
            out center;";

        var elements = await ExecuteOverpassQuery(query);
        if (elements == null) return;

        var pois = new List<PointOfInterest>();
        foreach (var el in elements.Value.EnumerateArray())
        {
            double lat, lon;
            if (el.TryGetProperty("center", out var c))
            {
                lat = c.GetProperty("lat").GetDouble();
                lon = c.GetProperty("lon").GetDouble();
            }
            else if (el.TryGetProperty("lat", out _))
            {
                lat = el.GetProperty("lat").GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }
            else continue;

            var tags = el.TryGetProperty("tags", out var t) ? t : default;
            var name = GetTag(tags, "name:ru") ?? GetTag(tags, "name") ?? "Водоём";
            var water = GetTag(tags, "water") ?? "";

            pois.Add(new PointOfInterest
            {
                Name = name, Type = "water",
                Latitude = lat, Longitude = lon,
                GeoLocation = new Point(lon, lat) { SRID = 4326 },
                Extra = water, UpdatedAt = DateTime.UtcNow
            });
        }
        await SavePois("water", pois);
    }

    public async Task UpdateForestsAsync()
    {
        _logger.LogInformation("Загрузка лесов Минска...");
        var query = @"
            [out:json];
            (
              way(53.82,27.35,54.00,27.75)[""landuse""=""forest""];
              way(53.82,27.35,54.00,27.75)[""natural""=""wood""];
              relation(53.82,27.35,54.00,27.75)[""landuse""=""forest""];
              relation(53.82,27.35,54.00,27.75)[""natural""=""wood""];
            );
            out center;";

        var elements = await ExecuteOverpassQuery(query);
        if (elements == null) return;

        var pois = new List<PointOfInterest>();
        foreach (var el in elements.Value.EnumerateArray())
        {
            double lat, lon;
            if (el.TryGetProperty("center", out var c))
            {
                lat = c.GetProperty("lat").GetDouble();
                lon = c.GetProperty("lon").GetDouble();
            }
            else if (el.TryGetProperty("lat", out _))
            {
                lat = el.GetProperty("lat").GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }
            else continue;

            var tags = el.TryGetProperty("tags", out var t) ? t : default;
            var name = GetTag(tags, "name:ru") ?? GetTag(tags, "name") ?? "Лес";

            pois.Add(new PointOfInterest
            {
                Name = name, Type = "forest",
                Latitude = lat, Longitude = lon,
                GeoLocation = new Point(lon, lat) { SRID = 4326 },
                UpdatedAt = DateTime.UtcNow
            });
        }
        await SavePois("forest", pois);
    }

    // ========== HELPERS ==========

    private async Task<JsonElement?> ExecuteOverpassQuery(string query)
    {
        try
        {
            var response = await _http.PostAsync(OverpassUrl,
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) }));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Overpass API ошибка: {Status}", response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("elements");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка запроса к Overpass API");
            return null;
        }
    }

    private async Task SavePois(string type, List<PointOfInterest> pois)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var old = await db.PointsOfInterest.Where(p => p.Type == type).ToListAsync();
        db.PointsOfInterest.RemoveRange(old);
        await db.PointsOfInterest.AddRangeAsync(pois);
        await db.SaveChangesAsync();
        _logger.LogInformation("POI тип '{Type}': загружено {Count} объектов", type, pois.Count);
    }

    private static string? GetTag(JsonElement tags, string key)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return null;
        return tags.TryGetProperty(key, out var val) ? val.GetString() : null;
    }
}
