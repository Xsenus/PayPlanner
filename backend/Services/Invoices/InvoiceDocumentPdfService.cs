using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace PayPlanner.Api.Services.Invoices;

public sealed class InvoiceDocumentPdfService : IInvoiceDocumentPdfService
{
    private const string BrowserPathConfigKey = "Pdf:BrowserPath";
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

        var executablePath = ResolveBrowserPath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "На сервере не найден Chromium/Chrome для генерации PDF. " +
                "Установите браузер или задайте путь через переменную PAYPLANNER_PDF_BROWSER_PATH.");
        }

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
            await page.SetContentAsync(html, new NavigationOptions
            {
                WaitUntil =
                [
                    WaitUntilNavigation.Load,
                    WaitUntilNavigation.Networkidle0,
                ],
            });

            return await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                PreferCSSPageSize = true,
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

    private string? ResolveBrowserPath()
    {
        var configuredPath = _configuration[BrowserPathConfigKey]
            ?? Environment.GetEnvironmentVariable("PAYPLANNER_PDF_BROWSER_PATH");

        if (IsExistingFile(configuredPath))
        {
            return configuredPath;
        }

        return CandidateBrowserPaths.FirstOrDefault(IsExistingFile);
    }

    private static bool IsExistingFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
