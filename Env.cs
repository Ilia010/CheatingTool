using System;

namespace CheatingTool;

/// <summary>Флаги отладки. Поддерживаются новые (CHEATINGTOOL_*) и старые (APPKA_*) имена.</summary>
internal static class Env
{
    public static bool Flag(string name) =>
        Environment.GetEnvironmentVariable("CHEATINGTOOL_" + name) == "1" ||
        Environment.GetEnvironmentVariable("APPKA_" + name) == "1";
}
