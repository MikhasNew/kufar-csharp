using RealEstateMinsk.Services;

namespace RealEstateMinsk.Data;

/// <summary>
/// Фоновый сервис для автоматического сбора данных с интервалом
/// </summary>
public class DataCollectionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DataCollectionService> _logger;
    private readonly IConfiguration _configuration;
    private Timer? _timer;
    private DateTime _lastRun = DateTime.MinValue;
    private bool _isRunning = false;

    public DateTime? LastSuccessfulRun { get; private set; }
    public int TotalListingsCollected { get; private set; }
    public string? LastError { get; private set; }

    public DataCollectionService(
        IServiceProvider serviceProvider,
        ILogger<DataCollectionService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Читаем интервал из конфигурации (по умолчанию 6 часов)
        var intervalHours = _configuration.GetValue<int>("DataCollection:IntervalHours", 6);
        var interval = TimeSpan.FromHours(intervalHours);

        _logger.LogInformation("DataCollectionService запущен с интервалом {Interval} часов", intervalHours);

        // Запускаем таймер
        _timer = new Timer(
            async _ => await CollectDataAsync(),
            null,
            interval, // Первый запуск через время интервала, а не сразу
            interval
        );

        return Task.CompletedTask;
    }

    private async Task CollectDataAsync()
    {
        // Предотвращаем параллельные запуски
        if (_isRunning)
        {
            _logger.LogWarning("Предыдущий сбор данных ещё не завершён, пропускаю");
            return;
        }

        _isRunning = true;
        LastError = null;

        try
        {
            _logger.LogInformation("Начат автоматический сбор данных...");
            _lastRun = DateTime.UtcNow;

            // Создаём scope для получения сервисов из DI
            using var scope = _serviceProvider.CreateScope();
            var scraper = scope.ServiceProvider.GetRequiredService<KufarScraper>();
            var listingService = scope.ServiceProvider.GetRequiredService<ListingService>();
            var investmentAnalyzer = scope.ServiceProvider.GetRequiredService<IInvestmentAnalyzer>();

            // Читаем глубину парсинга из конфигурации
            var maxPages = _configuration.GetValue<int>("DataCollection:MaxPagesPerRun", 5);
            _logger.LogInformation("Глубина парсинга: {MaxPages} страниц", maxPages);

            // Потоковый скрапинг данных — каждая страница обрабатывается сразу
            int totalReceived = 0;
            int totalSaved = 0;
            int totalUpdated = 0;
            bool hasNewListings = false;

            foreach (var category in new[] { "Квартира", "Дом" })
            {
                _logger.LogInformation("Запуск сбора для категории: {Category}", category);
                await foreach (var pageListings in scraper.ScrapeEnumerableAsync(maxPages, category))
                {
                    totalReceived += pageListings.Count;
                    _logger.LogDebug("Обработка страницы: {Count} объявлений", pageListings.Count);

                    var (newCount, updatedCount, savedListings) = await listingService.SaveListingsAsync(pageListings);
                    totalSaved += newCount;
                    totalUpdated += updatedCount;

                    if (newCount > 0) hasNewListings = true;

                    // Рассчитываем скоринги только для сохранённых объявлений этой страницы
                    if (savedListings.Count > 0)
                    {
                        await investmentAnalyzer.UpsertScoresAsync(savedListings);
                    }
                }
            }

            _logger.LogInformation("Потоковый сбор завершен: получено={Received}, новых={New}, обновлено={Updated}",
                totalReceived, totalSaved, totalUpdated);

            TotalListingsCollected += totalSaved;

            // После успешного сбора запускаем валидацию устаревших объявлений
            try
            {
                _logger.LogInformation("Запуск проверки устаревших объявлений на предмет закрытия...");
                var closedCount = await listingService.ValidateActiveListingsAsync(scraper, 100);
                _logger.LogInformation("Проверка завершена. Обнаружено закрытых объявлений: {Count}", closedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при проверке устаревших объявлений: {Message}", ex.Message);
            }

            // Автоматический пересчет инвестиционных скорингов если есть новые объявления
            if (hasNewListings)
            {
                _logger.LogInformation("Проверка алертов...");
                await CheckAlertsAsync(scope.ServiceProvider);
            }

            LastSuccessfulRun = DateTime.UtcNow;
            _logger.LogInformation("Автоматический сбор данных успешно завершен в {Time}", LastSuccessfulRun.Value.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Ошибка при автоматическом сборе данных: {Message}", ex.Message);
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <summary>
    /// Проверка активных алертов на соответствие новым объявлениям
    /// </summary>
    private async Task CheckAlertsAsync(IServiceProvider scopedProvider)
    {
        try
        {
            var listingService = scopedProvider.GetRequiredService<ListingService>();
            var investmentAnalyzer = scopedProvider.GetRequiredService<IInvestmentAnalyzer>();

            // Получаем активные алерты
            // TODO: Реализовать когда будет AlertService
            _logger.LogDebug("Проверка алертов пропущена (AlertService ещё не реализован)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при проверке алертов: {Message}", ex.Message);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DataCollectionService останавливается");
        _timer?.Change(Timeout.Infinite, 0);
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }
}
