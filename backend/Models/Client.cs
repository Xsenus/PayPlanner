using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Сущность клиента.
    /// </summary>
    public class Client
    {
        /// <summary>
        /// Почтовый адрес клиента.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Коллекция дел (кейсов) клиента.
        /// </summary>
        public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();

        /// <summary>
        /// Компания клиента (если применимо).
        /// </summary>
        public string Company { get; set; } = string.Empty;

        /// <summary>
        /// Внешний ключ на статус клиента.
        /// </summary>
        public int? ClientStatusId { get; set; }

        /// <summary>
        /// Навигация на статус клиента.
        /// </summary>
        public ClientStatus? ClientStatus { get; set; }

        public int? LegalEntityId { get; set; }

        public LegalEntity? LegalEntity { get; set; }

        /// <summary>
        /// Дата и время создания записи (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Электронная почта клиента.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Акты, связанные с клиентом.
        /// </summary>
        public ICollection<Act> Acts { get; set; } = new List<Act>();

        /// <summary>
        /// Уникальный идентификатор клиента.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Признак активного клиента.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Имя/название клиента.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Произвольные заметки по клиенту.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Коллекция платежей, связанных с клиентом.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        /// Телефон клиента.
        /// </summary>
        public string Phone { get; set; } = string.Empty;

        /// <summary>
        /// Договоры, связанные с клиентом.
        /// </summary>
        public ICollection<Contract> Contracts { get; set; } = new List<Contract>();

        /// <summary>
        /// Связи клиента с договорами.
        /// </summary>
        public ICollection<ClientContract> ClientContracts { get; set; } = new List<ClientContract>();
    }
}
