using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MorphocyteRouter;

public sealed partial class SubscriptionSource
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MorphocyteOS.Subscription.1");

    internal static SubscriptionSource Create(string url, DateTimeOffset? updatedAt = null)
    {
        _ = SubscriptionImporter.ValidateUrl(url);
        var bytes = Encoding.UTF8.GetBytes(url);
        try
        {
            return new SubscriptionSource { ProtectedUrl = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy,
                DataProtectionScope.CurrentUser)), UpdatedAt = updatedAt };
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal string ReadUrl()
    {
        if (ProtectedUrl.Length > 65536) throw new InvalidDataException("Сохранённая подписка недоступна. Укажи ссылку заново.");
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedUrl), Entropy, DataProtectionScope.CurrentUser);
            var url = Encoding.UTF8.GetString(bytes);
            _ = SubscriptionImporter.ValidateUrl(url);
            return url;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException)
        { throw new InvalidDataException("Сохранённая подписка недоступна. Укажи ссылку заново."); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
