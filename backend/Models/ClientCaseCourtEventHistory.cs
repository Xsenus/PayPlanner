using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourtEventHistory
{
    public int Id { get; set; }

    public int ClientCaseCourtEventId { get; set; }
    public ClientCaseCourtEvent ClientCaseCourtEvent { get; set; } = null!;

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Required, MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Summary { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public int? ActorUserId { get; set; }

    [MaxLength(256)]
    public string ActorUserName { get; set; } = string.Empty;
}
