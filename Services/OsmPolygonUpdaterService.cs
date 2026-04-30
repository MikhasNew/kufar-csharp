using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RealEstateMinsk.Services;

public class OsmPolygonUpdaterService
{
    private readonly HttpClient _http;
    private readonly ILogger<OsmPolygonUpdaterService> _logger;
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";

    public OsmPolygonUpdaterService(HttpClient http, ILogger<OsmPolygonUpdaterService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task UpdatePolygonsAsync(string outputPath)
    {
        _logger.LogInformation("Начало обновления полигонов районов Минска из OSM...");

        try
        {
            // Запрос всех административных районов Минска (admin_level=9)
            var query = @"
                [out:json];
                area[""name:en""=""Minsk""][""admin_level""=""4""]->.a;
                (
                  rel(area.a)[""admin_level""=""9""][""boundary""=""administrative""];
                );
                out body;
                >;
                out skel qt;";

            var response = await _http.PostAsync(OverpassUrl, new FormUrlEncodedContent(new[] 
            { 
                new KeyValuePair<string, string>("data", query) 
            }));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Ошибка запроса к Overpass API: {StatusCode}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(json);
            
            var nodes = new Dictionary<long, (double Lat, double Lon)>();
            var ways = new Dictionary<long, List<long>>();
            var relations = new List<OsmRelation>();

            foreach (var element in data.RootElement.GetProperty("elements").EnumerateArray())
            {
                var type = element.GetProperty("type").GetString();
                var id = element.GetProperty("id").GetInt64();

                if (type == "node")
                {
                    nodes[id] = (element.GetProperty("lat").GetDouble(), element.GetProperty("lon").GetDouble());
                }
                else if (type == "way")
                {
                    var wayNodes = element.GetProperty("nodes").EnumerateArray().Select(n => n.GetInt64()).ToList();
                    ways[id] = wayNodes;
                }
                else if (type == "relation")
                {
                    var name = element.GetProperty("tags").GetProperty("name:ru").GetString()?.Replace(" район", "") ?? "Unknown";
                    var members = element.GetProperty("members").EnumerateArray()
                        .Where(m => m.GetProperty("type").GetString() == "way" && m.GetProperty("role").GetString() == "outer")
                        .Select(m => m.GetProperty("ref").GetInt64())
                        .ToList();
                    relations.Add(new OsmRelation { Name = name, WayRefs = members });
                }
            }

            var resultPolygons = new Dictionary<string, List<(double Lat, double Lon)>>();

            foreach (var rel in relations)
            {
                var fullPolygon = new List<(double Lat, double Lon)>();
                
                // Склеиваем веи (пути) в один полигон
                // Для простоты предполагаем, что они идут по порядку (OSM обычно так отдает для outer)
                foreach (var wayRef in rel.WayRefs)
                {
                    if (ways.TryGetValue(wayRef, out var wayNodes))
                    {
                        foreach (var nodeRef in wayNodes)
                        {
                            if (nodes.TryGetValue(nodeRef, out var coord))
                            {
                                fullPolygon.Add(coord);
                            }
                        }
                    }
                }

                if (fullPolygon.Count > 0)
                {
                    // Упрощаем полигон алгоритмом Дугласа-Пекера
                    // Epsilon ~ 0.0001 дает хорошую точность при малом числе точек
                    var simplified = SimplifyDouglasPeucker(fullPolygon, 0.0001);
                    resultPolygons[rel.Name] = simplified;
                    _logger.LogInformation("Район {Name}: упрощено с {Original} до {Simplified} точек", rel.Name, fullPolygon.Count, simplified.Count);
                }
            }

            var finalJson = JsonSerializer.Serialize(resultPolygons.ToDictionary(
                kv => kv.Key, 
                kv => kv.Value.Select(p => new[] { p.Lat, p.Lon }).ToList()), 
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(outputPath, finalJson);
            _logger.LogInformation("Полигоны успешно сохранены в {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при обновлении полигонов");
        }
    }

    private List<(double Lat, double Lon)> SimplifyDouglasPeucker(List<(double Lat, double Lon)> points, double epsilon)
    {
        if (points.Count < 3) return points;

        int index = -1;
        double maxDist = 0;

        for (int i = 1; i < points.Count - 1; i++)
        {
            double dist = PerpendicularDistance(points[i], points[0], points[^1]);
            if (dist > maxDist)
            {
                index = i;
                maxDist = dist;
            }
        }

        if (maxDist > epsilon)
        {
            var res1 = SimplifyDouglasPeucker(points.GetRange(0, index + 1), epsilon);
            var res2 = SimplifyDouglasPeucker(points.GetRange(index, points.Count - index), epsilon);
            
            res1.RemoveAt(res1.Count - 1);
            res1.AddRange(res2);
            return res1;
        }
        else
        {
            return new List<(double Lat, double Lon)> { points[0], points[^1] };
        }
    }

    private double PerpendicularDistance((double Lat, double Lon) p, (double Lat, double Lon) lineStart, (double Lat, double Lon) lineEnd)
    {
        double dx = lineEnd.Lon - lineStart.Lon;
        double dy = lineEnd.Lat - lineStart.Lat;

        double mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag < 1e-10) return Math.Sqrt(Math.Pow(p.Lon - lineStart.Lon, 2) + Math.Pow(p.Lat - lineStart.Lat, 2));

        return Math.Abs(dy * p.Lon - dx * p.Lat + lineEnd.Lon * lineStart.Lat - lineEnd.Lat * lineStart.Lon) / mag;
    }

    private class OsmRelation
    {
        public string Name { get; set; } = "";
        public List<long> WayRefs { get; set; } = new();
    }
}
