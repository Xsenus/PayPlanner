using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class FormFieldRequirement
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Section { get; set; } = string.Empty;

    [MaxLength(128)]
    public string FieldKey { get; set; } = string.Empty;

    public bool IsRequired { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
