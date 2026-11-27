using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class StartupHook
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    public static void Initialize()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var path = Environment.GetEnvironmentVariable("PAYPLANNER_RESTORED_DIAGNOSTIC_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;

        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception.GetType().FullName != "Xunit.Sdk.TrueException") return;
            var message = args.Exception.Message;
            const string expected = "Не подтверждено чтение защищённых данных восстановленной CRM";
            if (!message.Contains("Isolated backend failed before listening:", StringComparison.Ordinal)) return;
            // Only report bounded diagnostics. Startup output can include protected fixture values.
            var report = JsonSerializer.Serialize(new
            {
                ManagedOutputCodePage = Console.OutputEncoding.CodePage,
                NativeOutputCodePage = GetConsoleOutputCP(),
                HasExpected = message.Contains(expected, StringComparison.Ordinal),
                CyrillicCharacters = message.Count(character => character is >= '\u0400' and <= '\u04ff'),
                ReplacementCharacters = message.Count(character => character == '\uFFFD'),
                QuestionRuns = Regex.Matches(message, @"\?{4,}").Count,
                Exceptions = new[] { "InvalidOperationException", "CryptographicException", "SocketException",
                    "FileNotFoundException", "IOException", "NpgsqlException", "ArgumentException" }
                    .Where(type => message.Contains(type, StringComparison.Ordinal)),
            });
            File.AppendAllText(path, report + Environment.NewLine);
        };
    }
}
