using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class CompanyBrandImageKinds
{
    public const string Logo = "logo";
    public const string Seal = "seal";
    public const string Signature = "signature";
}

/// <summary>
/// Неизменяемая версия фирменного изображения компании (логотип, печать, подпись), на которую ссылается снимок
/// продавца в выданном документе. Версия идентифицируется содержимым (SHA-256), добавляется один раз и не
/// изменяется и не удаляется при замене или удалении изображения в профиле, поэтому ранее выданные документы
/// остаются прежними. В JSON снимка попадает только номер версии, а не само изображение.
/// </summary>
public class CompanyBrandImageVersion
{
    [Key]
    public int Id { get; set; }

    /// <summary>Компания-владелец (ссылка без внешнего ключа: версия должна пережить архивацию и удаление профиля).</summary>
    public int LegalEntityId { get; set; }

    [Required, MaxLength(16)]
    public string Kind { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [Required]
    public string DataUrl { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
