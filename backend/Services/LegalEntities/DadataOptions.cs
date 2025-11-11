namespace PayPlanner.Api.Services.LegalEntities
{
    public sealed class DadataOptions
    {
        public string? ApiKey { get; set; }

        /// <summary
        /// >Базовый URL для подсказок / поиска компаний.
        /// </summary>
        public string BaseUrl { get; set; } = "https://suggestions.dadata.ru/suggestions/api/4_1/rs/";

        /// <summary>
        /// Базовый URL для cleaner API.
        /// </summary>
        public string CleanerUrl { get; set; } = "https://cleaner.dadata.ru/api/v1/clean/";

        public string? Secret { get; set; }

        /// <summary>
        /// Глобальный таймаут HTTP-клиента.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;
    }
}