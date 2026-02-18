using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class OkpdtrDictionaryItem
{
    [Key]
    public int Id { get; set; }

    [MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
