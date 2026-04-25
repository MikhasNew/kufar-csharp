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
    private string _currentCategory = "Квартира";
    private readonly int _maxRetries = 3;
    private readonly int _retryDelayMs = 2000;
    private readonly int _pageDelayMs;
    private readonly Random _random = new();

    private const string ApiBaseUrl = "https://api.kufar.by/search-api/v2/search/rendered-paginated";

    public KufarScraper(ILogger<KufarScraper>? logger, MinskGeoService geo, IConfiguration config)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KufarScraper>.Instance;
        _geo = geo;
        _pageDelayMs = config.GetValue<int>("DataCollection:PageDelayMs", 2000);
        
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
        var allListings = new List<Listing>();
        await foreach (var pageListings in ScrapeEnumerableAsync(maxPages))
        {
            allListings.AddRange(pageListings);
        }
        return allListings;
    }

    /// <summary>
    /// Потоковая обработка страниц — возвращает IAsyncEnumerable, где каждый элемент
    /// это список объявлений с одной загруженной страницы. Позволяет обрабатывать
    /// данные по мере поступления, не дожидаясь загрузки всех страниц.
    /// </summary>
    public async IAsyncEnumerable<List<Listing>> ScrapeEnumerableAsync(int maxPages = 2, string category = "Квартира")
    {
        var cat = category == "Дом" ? "1020" : "1010";
        var rgnValues = new[] { "7", "5" }; // 7 = Минск, 5 = Минская область

        _logger.LogInformation("Начат потоковый скрапинг Kufar, макс. страниц: {MaxPages}, категория: {Category}", maxPages, category);
        _currentCategory = category;

        foreach (var rgn in rgnValues)
        {
            string? currentCursor = null;
            int totalPagesLoaded = 0;

            for (int page = 1; page <= maxPages; page++)
            {
                var url = BuildApiUrl(currentCursor, cat, rgn);
            List<Listing>? pageListings = null;
            bool shouldBreak = false;

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
                    shouldBreak = true;
                }
                else
                {
                    var data = JsonDocument.Parse(responseMsg);

                    if (data.RootElement.TryGetProperty("total", out var total))
                    {
                        _logger.LogInformation("Всего найдено объявлений на Kufar: {Total}", total);
                    }

                    if (data.RootElement.TryGetProperty("ads", out var ads))
                    {
                        pageListings = new List<Listing>();
                        foreach (var ad in ads.EnumerateArray())
                        {
                            try
                            {
                                var listing = ParseListing(ad);
                                if (listing != null && listing.PriceUsd >= 1000 && listing.Area >= 5)
                                {
                                    pageListings.Add(listing);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Ошибка парсинга объявления на странице {Page}", page);
                            }
                        }

                        totalPagesLoaded++;
                        _logger.LogInformation("Страница {Page} (rgn={Rgn}): получено {Count} объявлений из {TotalOnPage}",
                            page, rgn, pageListings.Count, ads.GetArrayLength());

                        // Потоковый скрапинг: надежнее всего полагаться на наличие токена 'next' в пагинации,
                        // так как количество объявлений на странице может быть меньше 50 даже в середине списка.
                        string? nextToken = null;

                        if (data.RootElement.TryGetProperty("pagination", out var pagination) &&
                            pagination.TryGetProperty("pages", out var pagesArray))
                        {
                            foreach (var pElem in pagesArray.EnumerateArray())
                            {
                                if (pElem.TryGetProperty("label", out var labelProp) && labelProp.GetString() == "next")
                                {
                                    if (pElem.TryGetProperty("token", out var tokenProp))
                                    {
                                        nextToken = tokenProp.GetString();
                                    }
                                    break;
                                }
                            }
                        }

                        currentCursor = nextToken;

                        if (string.IsNullOrEmpty(currentCursor))
                        {
                            _logger.LogInformation("Токен следующей страницы не найден для rgn={Rgn}. Переходим к следующему региону.", rgn);
                            shouldBreak = true;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Отсутствует поле 'ads' в ответе API на странице {Page} rgn={Rgn}", page, rgn);
                        shouldBreak = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при запросе страницы {Page} rgn={Rgn}: {Message}", page, rgn, ex.Message);
                shouldBreak = true;
            }

            // Yield вне try-catch
            if (pageListings != null)
            {
                yield return pageListings;
            }

            if (shouldBreak)
            {
                // ВАЖНО: break (а не yield break!) — чтобы продолжить скрапинг следующего региона
                break;
            }

            if (page < maxPages)
            {
                // Добавляем случайную задержку ±20% для имитации поведения человека
                var jitter = _random.Next(-(int)(_pageDelayMs * 0.2), (int)(_pageDelayMs * 0.2));
                var delay = Math.Max(500, _pageDelayMs + jitter);
                
                _logger.LogDebug("Ожидание {Delay}мс перед следующей страницей...", delay);
                await Task.Delay(delay);
            }
        }

        _logger.LogInformation("Потоковый скрапинг завершен, загружено страниц: {Pages}", totalPagesLoaded);
        }
    }

    /// <summary>
    /// Формирует URL запроса к новому API Kufar
    /// </summary>
    private string BuildApiUrl(string? cursor, string cat, string rgn)
    {
        var size = 50;
        var url = $"{ApiBaseUrl}?cat={cat}&rgn={rgn}&cur=USD&gtsy=country-belarus&lang=ru&size={size}&typ=sell&sort=lst.d";
        
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
            // Если currency == BYR, то price_usd = 0 или содержит цену в BYR — пропускаем такие объявления
            var currency = ad.TryGetProperty("currency", out var currProp) ? (currProp.GetString() ?? "USD") : "USD";
            var priceUsdCents = ad.TryGetProperty("price_usd", out var priceProp)
                ? GetValueAsString(priceProp)
                : "0";

            if (!long.TryParse(priceUsdCents, out var priceCents))
            {
                return null;
            }

            var priceUsd = (int)(priceCents / 100);
            
            // Пропускаем объявления с ценой в BYR (нет USD-цены) или нулевой ценой
            if (currency == "BYR" || priceUsd == 0)
            {
                return null;
            }

            // ad_parameters → Площадь, координаты и другие параметры
            var area = 0.0;
            var rooms = 1;
            var floor = "";
            var floors = "";
            var yearBuilt = "";
            double? lat = null;
            double? lon = null;
            double? lotSize = null;
            string? wallMaterial = null;

            if (ad.TryGetProperty("ad_parameters", out var adParams))
            {
                foreach (var param in adParams.EnumerateArray())
                {
                    var p = param.TryGetProperty("p", out var pProp) ? pProp.GetString() : "";

                    if ((p == "size" || p == "size_total") && param.TryGetProperty("v", out var vProp))
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
                    else if ((p == "re_number_floors" || p == "house_number_floors") && param.TryGetProperty("v", out var vFloorsProp))
                    {
                        // Квартиры: re_number_floors; Дома: house_number_floors
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
                    else if ((p == "area_land" || p == "size_area") && param.TryGetProperty("v", out var vLotProp))
                    {
                        var lotStr = GetValueAsString(vLotProp);
                        double.TryParse(lotStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLot);
                        lotSize = parsedLot > 0 ? parsedLot : null;
                    }
                    else if (p == "material" && param.TryGetProperty("v", out var vMaterialProp))
                    {
                        wallMaterial = GetValueAsString(vMaterialProp);
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
                LotSize = lotSize,
                WallMaterial = wallMaterial,
                DistanceToMinsk = (lat.HasValue && lon.HasValue) ? Math.Round(_geo.GetDistanceToMinskCenter(lat.Value, lon.Value), 1) : null,
                ImageUrl = string.IsNullOrEmpty(imageUrl) ? null : imageUrl,
                District = ExtractDistrict(location, title, description),
                FlatType = GetFlatType(title, description),
                Location = location,
                Url = url,
                CreatedAt = created,
                ScrapedAt = DateTime.UtcNow.ToString("o"),
                Category = _currentCategory,
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
