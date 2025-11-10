namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Ñóùíîñòü êëèåíòà.
    /// </summary>
    public class Client
    {
        /// <summary>
        /// Ïî÷òîâûé àäðåñ êëèåíòà.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Êîëëåêöèÿ äåë (êåéñîâ) êëèåíòà.
        /// </summary>
        public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();

        /// <summary>
        /// Êîìïàíèÿ êëèåíòà (åñëè ïðèìåíèìî).
        /// </summary>
        public string Company { get; set; } = string.Empty;

        public int? LegalEntityId { get; set; }

        public LegalEntity? LegalEntity { get; set; }

        /// <summary>
        /// Äàòà è âðåìÿ ñîçäàíèÿ çàïèñè (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Ýëåêòðîííàÿ ïî÷òà êëèåíòà.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// <summary>
        ///  ,   .
        /// </summary>
        public ICollection<Act> Acts { get; set; } = new List<Act>();

        /// Óíèêàëüíûé èäåíòèôèêàòîð êëèåíòà.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Ïðèçíàê àêòèâíîãî êëèåíòà.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Èìÿ/íàçâàíèå êëèåíòà.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Ïðîèçâîëüíûå çàìåòêè ïî êëèåíòó.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Êîëëåêöèÿ ïëàòåæåé, ñâÿçàííûõ ñ êëèåíòîì.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        /// Òåëåôîí êëèåíòà.
        /// </summary>
        public string Phone { get; set; } = string.Empty;
    }
}