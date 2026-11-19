using System.Diagnostics;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace PayPlanner.Api.Services.Invoices;

public sealed class InvoiceDocumentPdfService : IInvoiceDocumentPdfService
{
    private const string BrowserPathConfigKey = "Pdf:BrowserPath";
    private const string BrowserCachePathConfigKey = "Pdf:BrowserCachePath";
    private static readonly string[] CandidateBrowserPaths =
    [
        "/usr/bin/chromium-browser",
        "/usr/bin/chromium",
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/snap/bin/chromium",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ];

    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoiceDocumentPdfService> _logger;
    private readonly SemaphoreSlim _browserResolveLock = new(1, 1);
    private string? _resolvedBrowserPath;

    public InvoiceDocumentPdfService(IConfiguration configuration, ILogger<InvoiceDocumentPdfService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("HTML-шаблон счета пуст, PDF сформировать невозможно.");
        }

        var executablePath = await ResolveBrowserPathAsync(ct);

        try
        {
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = executablePath,
                Args =
                [
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                ],
            });

            await using var page = await browser.NewPageAsync();
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = 1240,
                Height = 1754,
            });
            var contentReadiness = Stopwatch.StartNew();
            await page.SetContentAsync(html, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Load],
                Timeout = 30_000,
            });

            var remainingTimeout = 30_000 - (int)contentReadiness.ElapsedMilliseconds;
            if (remainingTimeout <= 0) throw new TimeoutException("PDF content readiness exceeded 30000 ms.");
            await page.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
            {
                Concurrency = 0,
                IdleTime = 500,
                Timeout = remainingTimeout,
            });

            return await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                PreferCSSPageSize = true,
                WaitForFonts = true,
            });
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render invoice PDF with browser {ExecutablePath}", executablePath);
            throw new InvalidOperationException(
                "Не удалось сформировать PDF-документ. Проверьте наличие установленного Chromium/Chrome на сервере.",
                ex);
        }
    }

    private async Task<string> ResolveBrowserPathAsync(CancellationToken ct)
    {
        if (IsExistingFile(_resolvedBrowserPath))
        {
            return _resolvedBrowserPath!;
        }

        var existingPath = FindExistingBrowserPath();
        if (existingPath is not null)
        {
            _resolvedBrowserPath = existingPath;
            return existingPath;
        }

        await _browserResolveLock.WaitAsync(ct);
        try
        {
            if (IsExistingFile(_resolvedBrowserPath))
            {
                return _resolvedBrowserPath!;
            }

            existingPath = FindExistingBrowserPath();
            if (existingPath is not null)
            {
                _resolvedBrowserPath = existingPath;
                return existingPath;
            }

            var downloadedPath = await DownloadBundledBrowserAsync();
            if (downloadedPath is not null)
            {
                _resolvedBrowserPath = downloadedPath;
                return downloadedPath;
            }
        }
        finally
        {
            _browserResolveLock.Release();
        }

        throw new InvalidOperationException(
            "На сервере не найден Chromium/Chrome для генерации PDF, и не удалось автоматически подготовить встроенный браузер. " +
            "Установите браузер или задайте путь через переменную PAYPLANNER_PDF_BROWSER_PATH.");
    }

    private string? FindExistingBrowserPath()
    {
        var configuredPath = _configuration[BrowserPathConfigKey]
            ?? Environment.GetEnvironmentVariable("PAYPLANNER_PDF_BROWSER_PATH");

        if (IsExistingFile(configuredPath))
        {
            return configuredPath;
        }

        return CandidateBrowserPaths.FirstOrDefault(IsExistingFile);
    }

    private async Task<string?> DownloadBundledBrowserAsync()
    {
        try
        {
            var browserCachePath = ResolveBrowserCachePath();
            Directory.CreateDirectory(browserCachePath);

            var fetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Browser = SupportedBrowser.Chrome,
                Path = browserCachePath,
            });

            var installedBrowser = fetcher
                .GetInstalledBrowsers()
                .FirstOrDefault();

            if (installedBrowser is not null)
            {
                var installedPath = installedBrowser.GetExecutablePath();
                if (IsExistingFile(installedPath))
                {
                    return installedPath;
                }
            }

            _logger.LogInformation(
                "No system browser found for invoice PDF generation. Downloading bundled browser to {BrowserCachePath}",
                browserCachePath);

            var downloadedBrowser = await fetcher.DownloadAsync(BrowserTag.Stable);
            var executablePath = downloadedBrowser.GetExecutablePath();

            if (IsExistingFile(executablePath))
            {
                _logger.LogInformation(
                    "Bundled browser for invoice PDF generation is ready at {ExecutablePath}",
                    executablePath);
                return executablePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download bundled browser for invoice PDF generation");
        }

        return null;
    }

    private string ResolveBrowserCachePath()
    {
        var configuredPath = _configuration[BrowserCachePathConfigKey]
            ?? Environment.GetEnvironmentVariable("PAYPLANNER_PDF_BROWSER_CACHE_PATH");

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "PayPlanner", "browser-cache");
        }

        return Path.Combine(Path.GetTempPath(), "PayPlanner", "browser-cache");
    }

    private static bool IsExistingFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
