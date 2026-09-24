using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Подтверждённая часть уже зарегистрированного платежа, относящаяся к исполнению судебного решения.
/// Не создаёт нового денежного движения и не изменяет учёт доходов.
/// </summary>
public sealed class ClientCaseCourtRecoveryPayment
{
    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Column(TypeName = "decimal(18,2)")]
    public decimal AllocatedAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}
