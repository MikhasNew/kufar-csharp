using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public class KufarScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<KufarScraper> _logger;
    private readonly MinskGeoService _geo;
    private readonly int _maxRetries = 3;
    private readonly int _retryDelayMs = 2000;

    private const string ApiBaseUrl = "https://api.kufar.by/search-api/v2/search/rendered-paginated";

    public KufarScraper(ILogger<KufarScraper>? logger, MinskGeoService geo)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KufarScraper>.Instance;
        _geo = geo;
        
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,ru-RU;q=0.8,ru;q=0.7");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<Listing>> ScrapeAsync(int maxPages = 2)
    {
        var listings = new List<Listing>();

        _logger.LogInformation("Начат скрапинг Kufar, страниц: {MaxPages}", maxPages);
        
        string? currentCursor = null;

        for (int page = 1; page <= maxPages; page++)
        {
            var url = BuildApiUrl(currentCursor);

            try
            {
                _logger.LogDebug("Запрос страницы {Page}/{MaxPages}: {Url}", page, maxPages, url);

                var responseMsg = await ExecuteWithRetry(async () =>
                {
                    var resp = await _http.GetAsync(url);
                    var content = await resp.Content.ReadAsStringAsync();
                    return content;
                });

                if (string.IsNullOrEmpty(responseMsg))
                {
                    _logger.LogWarning("Пустой ответ на странице {Page}", page);
                    break;
                }

                var data = JsonDocument.Parse(responseMsg);

                // Логируем общее количество найденных объявлений
                if (data.RootElement.TryGetProperty("total", out var total))
                {
                    _logger.LogInformation("Всего найдено объявлений на Kufar: {Total}", total);
                }

                if (!data.RootElement.TryGetProperty("ads", out var ads))
                {
                    _logger.LogWarning("Отсутствует поле 'ads' в ответе API на странице {Page}", page);
                    break;
                }

                int pageSavedCount = 0;
                foreach (var ad in ads.EnumerateArray())
                {
                    try
                    {
                        var listing = ParseListing(ad);
                        if (listing != null && listing.PriceUsd >= 1000 && listing.Area >= 5)
                        {
                            listings.Add(listing);
                            pageSavedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка парсинга объявления на странице {Page}", page);
                    }
                }

                _logger.LogInformation("Страница {Page}: сохранено {Count} объявлений из {TotalOnPage}", 
                    page, pageSavedCount, ads.GetArrayLength());

                // Получаем токен для следующей страницы
                currentCursor = null;
                if (data.RootElement.TryGetProperty("pagination", out var pagination) &&
                    pagination.TryGetProperty("pages", out var pagesArray))
                {
                    foreach (var pElem in pagesArray.EnumerateArray())
                    {
                        if (pElem.TryGetProperty("label", out var labelProp) && labelProp.GetString() == "next")
                        {
                            if (pElem.TryGetProperty("token", out var tokenProp))
                            {
                                currentCursor = tokenProp.GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(currentCursor))
                {
                    _logger.LogInformation("Достигнута последняя страница. Завершаем скрапинг.");
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при запросе страницы {Page}: {Message}", page, ex.Message);
                break;
            }

            if (page < maxPages)
            {
                await Task.Delay(2000);
            }
        }

        _logger.LogInformation("Скрапинг завершен, всего получено {Count} объявлений", listings.Count);
        return listings;
    }

    /// <summary>
    /// Формирует URL запроса к новому API Kufar
    /// </summary>
    private string BuildApiUrl(string? cursor)
    {
        var size = 50;
        var url = $"{ApiBaseUrl}?cat=1010&cur=USD&gtsy=country-belarus~province-minsk&lang=ru&size={size}&typ=sell&sort=lst.d";
        
        if (!string.IsNullOrEmpty(cursor))
        {
            url += $"&cursor={cursor}";
        }

        return url;
    }

    /// <summary>
    /// Выполняет HTTP запрос с повторными попытками при ошибке
    /// </summary>
    private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> action)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                _logger.LogWarning("Попытка {Attempt}/{MaxRetries} не удалась: {Message}",
                    attempt, _maxRetries, ex.Message);
                await Task.Delay(_retryDelayMs * attempt);
            }
            catch (TaskCanceledException ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                _logger.LogWarning("Таймаут на попытке {Attempt}/{MaxRetries}: {Message}",
                    attempt, _maxRetries, ex.Message);
                await Task.Delay(_retryDelayMs * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка при запросе: {Message}", ex.Message);
                throw;
            }
        }

        throw new HttpRequestException($"Не удалось выполнить запрос после {_maxRetries} попыток", lastException);
    }

    /// <summary>
    /// Парсинг одного объявления из новой структуры JSON
    /// </summary>
    private Listing? ParseListing(JsonElement ad)
    {
        try
        {
            // ad_id → ID объявления
            var externalId = ad.TryGetProperty("ad_id", out var adIdProp)
                ? adIdProp.GetRawText().Trim('"')
                : Guid.NewGuid().ToString();

            // subject → Заголовок
            var title = ad.TryGetProperty("subject", out var titleProp)
                ? GetValueAsString(titleProp)
                : "";

            if (string.IsNullOrEmpty(title))
            {
                return null;
            }

            // price_usd → Цена (строка в центах, нужно разделить на 100)
            var priceUsdCents = ad.TryGetProperty("price_usd", out var priceProp)
                ? GetValueAsString(priceProp)
                : "0";

            if (!long.TryParse(priceUsdCents, out var priceCents))
            {
                return null;
            }

            var priceUsd = (int)(priceCents / 100);

            // ad_parameters → Площадь, координаты и другие параметры
            var area = 0.0;
            var rooms = 1;
            var floor = "";
            var floors = "";
            var yearBuilt = "";
            double? lat = null;
            double? lon = null;

            if (ad.TryGetProperty("ad_parameters", out var adParams))
            {
                foreach (var param in adParams.EnumerateArray())
                {
                    var p = param.TryGetProperty("p", out var pProp) ? pProp.GetString() : "";

                    if (p == "size" && param.TryGetProperty("v", out var vProp))
                    {
                        var sizeStr = GetValueAsString(vProp);
                        double.TryParse(sizeStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out area);
                    }
                    else if (p == "rooms" && param.TryGetProperty("v", out var vRoomsProp))
                    {
                        var roomsStr = GetValueAsString(vRoomsProp);
                        int.TryParse(roomsStr, out rooms);
                    }
                    else if (p == "floor" && param.TryGetProperty("v", out var vFloorProp))
                    {
                        // Новый API: floor может быть массивом [3]
                        floor = GetValueAsString(vFloorProp);
                    }
                    else if (p == "re_number_floors" && param.TryGetProperty("v", out var vFloorsProp))
                    {
                        // Новый API: re_number_floors вместо floors (может быть массивом)
                        floors = GetValueAsString(vFloorsProp);
                    }
                    else if (p == "year_built" && param.TryGetProperty("v", out var vYearProp))
                    {
                        // Новый API: year_built вместо build_year
                        yearBuilt = GetValueAsString(vYearProp);
                    }
                    else if (p == "coordinates" && param.TryGetProperty("v", out var coordsProp) && coordsProp.ValueKind == JsonValueKind.Array)
                    {
                        // Координаты теперь в ad_parameters: [lon, lat]
                        var coords = coordsProp.EnumerateArray().ToList();
                        if (coords.Count >= 2)
                        {
                            lon = coords[0].GetDouble();
                            lat = coords[1].GetDouble();
                            _logger.LogDebug("Извлечены координаты: lat={Lat}, lon={Lon}", lat, lon);
                        }
                    }
                    // Пропускаем параметры с массивами (metro и т.д.)
                }
            }

            if (area == 0)
            {
                return null;
            }

            // Изображения
            var imageUrl = "";
            if (ad.TryGetProperty("images", out var imagesProp) && imagesProp.ValueKind == JsonValueKind.Array && imagesProp.GetArrayLength() > 0)
            {
                var firstImage = imagesProp[0];
                if (firstImage.TryGetProperty("path", out var pathProp))
                {
                    var path = pathProp.GetString();
                    if (!string.IsNullOrEmpty(path) && path.Length > 2)
                    {
                        imageUrl = $"https://yams.kufar.by/api/v1/kufar-ads/images/{path.Substring(0, 2)}/{path}";
                    }
                }
            }

            // account_parameters → Локация/адрес
            var location = "";
            if (ad.TryGetProperty("account_parameters", out var accountParams))
            {
                foreach (var param in accountParams.EnumerateArray())
                {
                    var p = param.TryGetProperty("p", out var pProp) ? pProp.GetString() : "";
                    if (p == "address" && param.TryGetProperty("v", out var vProp))
                    {
                        location = GetValueAsString(vProp);
                    }
                }
            }

            // ad_link → Ссылка на объявление
            var url = ad.TryGetProperty("ad_link", out var urlProp)
                ? GetValueAsString(urlProp)
                : "";

            // body_short → Краткое описание
            var description = ad.TryGetProperty("body_short", out var descProp)
                ? GetValueAsString(descProp)
                : "";

            // list_time → Дата создания
            var created = ad.TryGetProperty("list_time", out var ctProp)
                ? GetValueAsString(ctProp)
                : "";

            int.TryParse(floor, out var parsedFloor);
            int.TryParse(floors, out var parsedFloors);
            int.TryParse(yearBuilt, out var parsedYear);

            return new Listing
            {
                ExternalId = externalId,
                Title = title,
                Description = description,
                PriceUsd = priceUsd,
                Area = Math.Round(area, 2),
                PricePerSqm = area > 0 ? Math.Round((double)priceUsd / area, 2) : 0,
                Rooms = rooms,
                YearBuilt = parsedYear > 0 ? parsedYear : null,
                Floor = parsedFloor > 0 ? parsedFloor : null,
                TotalFloors = parsedFloors > 0 ? parsedFloors : null,
                Latitude = lat,
                Longitude = lon,
                ImageUrl = string.IsNullOrEmpty(imageUrl) ? null : imageUrl,
                District = ExtractDistrict(location, title, description),
                FlatType = GetFlatType(title, description),
                Location = location,
                Url = url,
                CreatedAt = created,
                ScrapedAt = DateTime.UtcNow.ToString("o"),
                IsInteresting = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка парсинга объявления: {Message}", ex.Message);
            return null;
        }
    }

    private string ExtractDistrict(string location, string title, string description)
    {
        // Сначала проверяем адрес
        var d = _geo.GetDistrictByAddress(location);
        if (!string.IsNullOrEmpty(d) && d != "Unknown") return d;

        // Затем заголовок
        d = _geo.GetDistrictByAddress(title);
        if (!string.IsNullOrEmpty(d) && d != "Unknown") return d;

        // В конце описание (может быть длинным, но там часто указан микрорайон)
        d = _geo.GetDistrictByAddress(description);
        if (!string.IsNullOrEmpty(d) && d != "Unknown") return d;

        return "Unknown";
    }

    private string GetFlatType(string title, string desc)
    {
        var text = (title + " " + desc).ToLower();
        if (text.Contains("хрущевк")) return "Хрущевка";
        if (text.Contains("брежневк")) return "Брежневка";
        if (text.Contains("панел")) return "Панельный";
        if (text.Contains("кирпич")) return "Кирпичный";
        if (text.Contains("каркас")) return "Каркасно-блочный";
        if (text.Contains("новостройк")) return "Новостройка";
        return "Другой";
    }

    private string GetValueAsString(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Number:
                return element.GetRawText();
            case JsonValueKind.Array:
                var array = element.EnumerateArray();
                return array.Any() ? GetValueAsString(array.First()) : "";
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return "";
        }
    }
}
