using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class OkinDictionaryItem
{
    [Key]
    public int Id { get; set; }

    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
