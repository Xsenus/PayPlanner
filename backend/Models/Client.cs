namespace PayPlanner.Api.Models
{
    /// <summary>
    ///  .
    /// </summary>
    public class Client
    {
        /// <summary>
        ///   .
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        ///   () .
        /// </summary>
        public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();

        /// <summary>
        ///   ( ).
        /// </summary>
        public string Company { get; set; } = string.Empty;

        /// <summary>
        ///      (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        ///   .
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        ///   .
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        ///   .
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// / .
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        ///    .
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        ///  ,   .
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        ///  .
        /// </summary>
        public string Phone { get; set; } = string.Empty;

        /// <summary>
        /// ,    .
        /// </summary>
        public ICollection<CompanyClient> CompanyMemberships { get; set; } = new List<CompanyClient>();
    }
}
