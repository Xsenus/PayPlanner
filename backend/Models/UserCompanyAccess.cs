namespace PayPlanner.Api.Models;

public class UserCompanyAccess
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int LegalEntityId { get; set; }
    public LegalEntity LegalEntity { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
