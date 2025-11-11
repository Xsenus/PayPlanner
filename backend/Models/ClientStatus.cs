using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Справочная сущность со статусом клиента.
    /// </summary>
    public class ClientStatus
    {
        /// <summary>
        /// Цвет бейджа статуса в HEX-формате (#RRGGBB).
        /// </summary>
        public string ColorHex { get; set; } = "#2563EB";

        /// <summary>
        /// Дата и время создания записи (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Текстовое описание статуса.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Уникальный идентификатор статуса.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активного статуса.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Наименование статуса.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Коллекция клиентов, у которых выбран данный статус.
        /// </summary>
        public ICollection<Client> Clients { get; set; } = new List<Client>();
    }
}
