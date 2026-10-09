using System.IO;
using System.Security.Cryptography;

namespace MorphocyteRouter;

internal static class BundledResources
{
    internal const string CoreVersion = "1.19.31";
    internal const string CoreHash = "4A2275F385FC11106F7D819C16B510FCCA5E4996B5E902D295BFC78AC29C5530";
    internal const string CoreFileName = "mihomo.exe";
    internal static Stream Open(string name) => typeof(BundledResources).Assembly.GetManifestResourceStream("MorphocyteOS." + name)
        ?? throw new IOException("В сборке отсутствует ресурс: " + name);
    internal static string TemplateText
    {
        get { using var reader = new StreamReader(Open("profile-template.yaml")); return reader.ReadToEnd(); }
    }

    internal static async Task<string> EnsureCoreAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(AppContext.BaseDirectory, CoreFileName);
        if (!File.Exists(target))
            throw new FileNotFoundException("Рядом с MorphocyteOS.exe не найден mihomo.exe. Распакуй оба файла в одну папку или выбери ядро в настройках.", target);
        if (!await HasExpectedHashAsync(target, token))
            throw new InvalidDataException("Контрольная сумма mihomo.exe не совпадает с версией, поставляемой с MorphocyteOS.");
        return target;
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)) == CoreHash;
    }

    internal static async Task PrepareDefaultsAsync(AppSettings settings, CancellationToken token)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, CoreFileName);
        var legacyBundledPath = Path.Combine(settings.StorageDirectory, "Engine", "mihomo-" + CoreVersion, CoreFileName);
        if (string.IsNullOrWhiteSpace(settings.CorePath) || !File.Exists(settings.CorePath)
            || settings.CoreDefaultsVersion < 1 && (
                string.Equals(Path.GetFullPath(settings.CorePath), bundledPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(settings.CorePath), legacyBundledPath, StringComparison.OrdinalIgnoreCase))
                && await HasExpectedHashAsync(settings.CorePath, token))
            settings.CorePath = CoreBackend.BundledSingBoxPath;
        settings.CoreDefaultsVersion = 1;
        if (string.IsNullOrWhiteSpace(settings.ConfigPath))
        {
            var path = Path.Combine(settings.StorageDirectory, "Profiles", "default.yaml");
            await Task.Run(() =>
            {
                if (!File.Exists(path)) AtomicFile.Write(path, TemplateText);
            }, token);
            settings.ConfigPath = path;
        }
    }
}
