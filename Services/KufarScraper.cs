using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Services;

public class KufarScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<KufarScraper> _logger;
    private readonly int _maxRetries = 3;
    private readonly int _retryDelayMs = 2000;

    // Новый API endpoint Kufar
    private const string ApiBaseUrl = "https://api.kufar.by/search-api/v2/search/rendered-paginated";

    private readonly Dictionary<string, string[]> _districtKeywords = new()
    {
        ["Фрунзенский"] = new[] { "фрунзенский" },
        ["Московский"] = new[] { "московский" },
        ["Октябрьский"] = new[] { "октябрьский" },
        ["Советский"] = new[] { "советский" },
        ["Центральный"] = new[] { "центральный" },
        ["Первомайский"] = new[] { "первомайский" },
        ["Ленинский"] = new[] { "ленинский" },
        ["Партизанский"] = new[] { "партизанский" },
        ["Заводской"] = new[] { "заводской" }
    };

    public KufarScraper(ILogger<KufarScraper>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KufarScraper>.Instance;
        
        // Используем SocketsHttpHandler для более реалистичного TLS fingerprint
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
        _http.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not A(Brand\";v=\"99\", \"Google Chrome\";v=\"121\", \"Chromium\";v=\"121\"");
        _http.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        _http.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<Listing>> ScrapeAsync(int maxPages = 2)
    {
        var listings = new List<Listing>();

        _logger.LogInformation("Начат скрапинг Kufar (новый API), страниц: {MaxPages}", maxPages);
        
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
                    
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("HTTP {StatusCode}: {Content}", resp.StatusCode, content.Substring(0, Math.Min(500, content.Length)));
                    }
                    else
                    {
                        _logger.LogDebug("HTTP OK, размер ответа: {Length} байт, первых 200 символов: {Preview}", content.Length, content.Substring(0, Math.Min(200, content.Length)));
                    }
                    
                    return content;
                });

                if (string.IsNullOrEmpty(responseMsg))
                {
                    _logger.LogWarning("Пустой ответ на странице {Page}", page);
                    break;
                }

                var data = JsonDocument.Parse(responseMsg);

                if (!data.RootElement.TryGetProperty("ads", out var ads))
                {
                    _logger.LogWarning("Отсутствует поле 'ads' в ответе API на странице {Page}", page);
                    break;
                }

                int pageListingsCount = 0;
                foreach (var ad in ads.EnumerateArray())
                {
                    try
                    {
                        var listing = ParseListing(ad);
                        if (listing != null)
                        {
                            _logger.LogDebug("Объявление: {Title}, Цена: ${Price}, Площадь: {Area}", 
                                listing.Title, listing.PriceUsd, listing.Area);
                            
                            // Фильтр: цена от $1,000 и площадь от 5 м²
                            if (listing.PriceUsd >= 1000 && listing.Area >= 5)
                            {
                                listings.Add(listing);
                                pageListingsCount++;
                            }
                            else
                            {
                                _logger.LogDebug("Отфильтровано: цена ${Price} или площадь {Area}", 
                                    listing.PriceUsd, listing.Area);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка парсинга объявления на странице {Page}", page);
                    }
                }

                _logger.LogDebug("Страница {Page}: получено {Count} объявлений", page, pageListingsCount);

                // Если объявлений меньше чем size — значит это последняя страница
                if (pageListingsCount < 50)
                {
                    _logger.LogInformation("Достигнута последняя страница ({Count} < 50)", pageListingsCount);
                    break;
                }

                // Получаем токен для следующей страницы
                currentCursor = null;
                if (data.RootElement.TryGetProperty("pagination", out var pagination) &&
                    pagination.TryGetProperty("pages", out var pagesArray))
                {
                    foreach (var pElem in pagesArray.EnumerateArray())
                    {
                        if (pElem.TryGetProperty("label", out var labelProp) && labelProp.GetString() == "next")
                        {
                            if (pElem.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
                            {
                                currentCursor = tokenProp.GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(currentCursor))
                {
                    _logger.LogInformation("Токен следующей страницы не найден. Завершаем скрапинг.");
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при запросе страницы {Page}: {Message}", page, ex.Message);
                break;
            }

            // Задержка между страницами для предотвращения блокировки
            if (page < maxPages)
            {
                await Task.Delay(3000);
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

            // ad_parameters → Площадь и другие параметры
            var area = 0.0;
            var rooms = 1;
            var floor = "";
            var floors = "";
            var yearBuilt = "";

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
                        floor = GetValueAsString(vFloorProp);
                    }
                    else if (p == "floors" && param.TryGetProperty("v", out var vFloorsProp))
                    {
                        floors = GetValueAsString(vFloorsProp);
                    }
                    else if (p == "build_year" && param.TryGetProperty("v", out var vYearProp))
                    {
                        yearBuilt = GetValueAsString(vYearProp);
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

            // account_parameters → Локация/адрес и Координаты
            var location = "";
            double? lat = null;
            double? lon = null;
            if (ad.TryGetProperty("account_parameters", out var accountParams))
            {
                foreach (var param in accountParams.EnumerateArray())
                {
                    var p = param.TryGetProperty("p", out var pProp) ? pProp.GetString() : "";
                    if (p == "address" && param.TryGetProperty("v", out var vProp))
                    {
                        location = GetValueAsString(vProp);
                    }
                    else if (p == "coordinates" && param.TryGetProperty("v", out var coordsProp) && coordsProp.ValueKind == JsonValueKind.Array)
                    {
                        var coords = coordsProp.EnumerateArray().ToList();
                        if (coords.Count == 2)
                        {
                            lon = coords[0].GetDouble();
                            lat = coords[1].GetDouble();
                        }
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
                District = ExtractDistrict(location),
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

    private string ExtractDistrict(string location)
    {
        var l = location.ToLower();
        foreach (var (district, keywords) in _districtKeywords)
            if (keywords.Any(k => l.Contains(k)))
                return district;
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
