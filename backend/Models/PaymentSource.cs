using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Payment source (bank transfer, cash and so on).
    /// </summary>
    public class PaymentSource
    {
        /// <summary>
        /// Color in HEX format.
        /// </summary>
        public string ColorHex { get; set; } = "#6B7280";

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optional description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Identifier.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Active flag.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Display name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Direction of payments where the source is applicable (income or expense).
        /// </summary>
        public PaymentType? PaymentType { get; set; }

        /// <summary>
        /// Payments linked with this source.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
