using System;
using System.Text;

public static class StartupHook
{
    public static void Initialize() => Console.OutputEncoding = new UTF8Encoding(false);
}
