namespace PayPlanner.Api.Models.Requests;

public class UpdateUserActivitySettingsRequest
{
    public bool IsEnabled { get; set; } = true;
    public int? RetentionDays { get; set; }
}
