using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

public class ClientCaseWorkOrderItem
{
    public int Id { get; set; }

    public int ClientCaseWorkOrderId { get; set; }
    public ClientCaseWorkOrder WorkOrder { get; set; } = null!;

    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public int? ExecutorUserId { get; set; }
    public User? ExecutorUser { get; set; }

    [MaxLength(256)]
    public string ExecutorName { get; set; } = string.Empty;

    public DateTime? DueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    public int SortOrder { get; set; }
}
