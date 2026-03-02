namespace PayPlanner.Api.Models;

/// <summary>
/// Тип документа в платежном реестре.
/// </summary>
public enum DocumentKind
{
    /// <summary>
    /// Счет.
    /// </summary>
    Invoice = 0,

    /// <summary>
    /// Начисление.
    /// </summary>
    Accrual = 1,

    /// <summary>
    /// Оплата (факт движения денег).
    /// </summary>
    Payment = 2,
}

