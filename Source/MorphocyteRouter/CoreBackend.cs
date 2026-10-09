using System.IO;
using System.Security.Cryptography;

namespace MorphocyteRouter;

internal static class CoreBackend
{
    internal const string SingBoxVersion = "1.14.2";
    internal const string SingBoxFileName = "sing-box.exe";
    internal const string SingBoxHash = "7BBEF1DEA9189EE12799AE834EA4B4658355DA25C47A21AD8804904C0CCD9410";
    // Stable releases before 1.2.7 allow managed files in ThirdPartySource but
    // reject a new root executable. Keep the bundled engine update-compatible;
    // root fallback also supports existing test/custom installations.
    internal static string BundledSingBoxPath => File.Exists(Path.Combine(AppContext.BaseDirectory, "ThirdPartySource", "Engine", SingBoxFileName))
        ? Path.Combine(AppContext.BaseDirectory, "ThirdPartySource", "Engine", SingBoxFileName)
        : Path.Combine(AppContext.BaseDirectory, SingBoxFileName);
    internal static bool IsSingBox(string path) => Path.GetFileNameWithoutExtension(path).Equals("sing-box", StringComparison.OrdinalIgnoreCase);
    internal static string Name(string path) => IsSingBox(path) ? "sing-box" : "Mihomo";

    internal static async Task<string> EnsureSingBoxAsync(CancellationToken token = default)
    {
        var path = BundledSingBoxPath;
        if (!File.Exists(path)) throw new IOException("В комплекте приложения не найден sing-box.exe. Распакуй весь архив релиза.");
        await using var input = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != SingBoxHash)
            throw new IOException("Контрольная сумма комплектного sing-box не совпала. Выбор ядра отменён.");
        return path;
    }

    internal static string RuntimeText(string executable, ClashConfigDocument document, CoreDiagnostics api, bool fullTunnel, int speedPort, string? selectedServer = null)
        => IsSingBox(executable) ? SingBoxConfig.Build(document, api.Port, api.Secret, fullTunnel, speedPort, selectedServer)
            : document.BuildRuntimeText(api.Port, api.Secret, fullTunnel, speedPort);
}
