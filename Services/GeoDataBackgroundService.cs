using Microsoft.EntityFrameworkCore;
using RealEstateMinsk.Data;

namespace RealEstateMinsk.Services;

/// <summary>
/// Фоновый сервис: обновляет полигоны районов и POI (метро, парки, водоёмы, леса) из OSM.
/// Запускается при старте (если данных нет) и далее раз в 30 дней.
/// </summary>
public class GeoDataBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GeoDataBackgroundService> _logger;
    private readonly IConfiguration _config;

    public GeoDataBackgroundService(IServiceProvider serviceProvider, ILogger<GeoDataBackgroundService> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ждём 5 секунд после старта, чтобы не конкурировать с инициализацией
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var intervalDays = _config.GetValue<int>("DataCollection:GeoUpdateIntervalDays", 30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateGeoDataIfNeeded(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в GeoDataBackgroundService");
            }

            await Task.Delay(TimeSpan.FromDays(intervalDays), stoppingToken);
        }
    }

    private async Task UpdateGeoDataIfNeeded(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();

        // 1. Полигоны районов
        var polygonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minsk_polygons.json");
        if (!File.Exists(polygonPath))
        {
            _logger.LogInformation("Файл полигонов не найден — запускаем обновление из OSM...");
            var osmUpdater = scope.ServiceProvider.GetRequiredService<OsmPolygonUpdaterService>();
            await osmUpdater.UpdatePolygonsAsync(polygonPath);
        }
        else
        {
            _logger.LogInformation("Файл полигонов существует, пропускаем.");
        }

        // 2. Все POI (метро, парки, водоёмы, леса)
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var poiCount = await db.PointsOfInterest.CountAsync(ct);
        if (poiCount == 0)
        {
            _logger.LogInformation("POI не найдены — загружаем все типы из OSM...");
            var poiUpdater = scope.ServiceProvider.GetRequiredService<PoiUpdaterService>();
            await poiUpdater.UpdateAllPoiAsync();
        }
        else
        {
            _logger.LogInformation("POI уже загружены ({Count} записей), пропускаем.", poiCount);
        }
    }
}
