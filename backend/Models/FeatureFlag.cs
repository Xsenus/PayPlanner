using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

[Table("feature_flags")]
public class FeatureFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("flag_key")]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [Column("enabled")]
    public bool Enabled { get; set; } = false;

    [Column("description")]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class FeatureFlags
{
    public const string ExpensesModule = "expenses_module_enabled";
    public const string ClientsSplit = "clients_split_enabled";
    public const string PartialPayments = "partial_payments_enabled";
    public const string ActivityLoggingUIClicks = "activity_logging_ui_clicks";
    public const string EmployeesDirectory = "employees_directory_enabled";
}
