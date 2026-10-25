using System.Globalization;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal static class MaxTaskNotificationPresentation
{
    internal static Uri? FrontendUri(IConfiguration? configuration)
    {
        var value = (configuration?["App:FrontendUrl"] ?? configuration?["Frontend:BaseUrl"]
            ?? configuration?["Frontend:PublicUrl"])?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? uri : null;
    }

    internal static Uri TaskUri(Uri frontend, int taskId) => new(frontend, $"/tasks/{taskId.ToString(CultureInfo.InvariantCulture)}");

    internal static bool TryTaskId(Uri? uri, Uri? frontend, out int taskId)
    {
        taskId = 0;
        return uri is { IsAbsoluteUri: true } && frontend is not null
            && uri.Scheme == frontend.Scheme && uri.IdnHost == frontend.IdnHost && uri.Port == frontend.Port
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.StartsWith("/tasks/", StringComparison.Ordinal)
            && int.TryParse(uri.AbsolutePath[7..], NumberStyles.None, CultureInfo.InvariantCulture, out taskId)
            && taskId > 0 && uri.AbsoluteUri == TaskUri(frontend, taskId).AbsoluteUri;
    }

    internal static string Render(string title, string notificationType, Uri uri)
    {
        var clean = new StringBuilder();
        var pendingSpace = false;
        foreach (var rune in title.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.Control)
            {
                pendingSpace = clean.Length > 0;
                continue;
            }
            // Убираем управляющие направлением письма знаки, сохраняя ZWJ в emoji.
            if (category == UnicodeCategory.Format && rune.Value != 0x200D) continue;
            if (pendingSpace) clean.Append(' ');
            pendingSpace = false;
            clean.Append(rune.ToString());
        }
        var normalized = clean.ToString();
        var boundaries = StringInfo.ParseCombiningCharacters(normalized);
        if (boundaries.Length > 160) normalized = normalized[..boundaries[160]] + "…";
        if (normalized.Length == 0) normalized = "Без названия";
        var change = UserNotificationTypes.Find(notificationType)!.Label;
        return $"Задача: {normalized}\n{change}\n{uri.AbsoluteUri}";
    }
}
