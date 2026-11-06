using System.Globalization;
using System.Net.Http;
using System.Text;

namespace MorphocyteRouter;

public sealed class SubscriptionMetadata
{
    public string Title { get; set; } = "";
    public string Announcement { get; set; } = "";
    public long? Upload { get; set; }
    public long? Download { get; set; }
    public long? Total { get; set; }
    public long? Expires { get; set; }
    public DateTimeOffset CheckedAt { get; set; }

    internal static SubscriptionMetadata FromResponse(HttpResponseMessage response)
    {
        string Header(string name) => response.Headers.TryGetValues(name,out var values)?values.FirstOrDefault()??"":"";
        var result=new SubscriptionMetadata { Title=Decode(Header("profile-title"),256),Announcement=Decode(Header("announce"),4096),CheckedAt=DateTimeOffset.UtcNow };
        foreach(var field in Header("subscription-userinfo").Split(';'))
        {
            var pair=field.Trim().Split('=',2);
            if(pair.Length!=2 || !long.TryParse(pair[1].Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var number) || number<0) continue;
            switch(pair[0].Trim().ToLowerInvariant())
            {
                case "upload": result.Upload=number; break;
                case "download": result.Download=number; break;
                case "total": result.Total=number; break;
                case "expire" when number is >0 and <=253402300799: result.Expires=number; break;
            }
        }
        return result;
    }

    private static string Decode(string text,int max)
    {
        if(text.Length>16384) return "";
        if(text.StartsWith("base64:",StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded=text[7..].Replace('-','+').Replace('_','/');
                text=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length+3)/4*4,'=')));
            }
            catch { return ""; }
        }
        text=new string(text.Where(ch=>!char.IsControl(ch)||ch=='\n').ToArray()).Trim();
        return text.Length>max?text[..max]:text;
    }

    internal string TrafficLabel => Upload is not null || Download is not null
        ?Bytes((decimal)(Upload??0)+(Download??0))+" / "+(Total is >0?Bytes(Total.Value):Total==0?"без лимита":"лимит не указан") :"Трафик не указан";
    internal string ExpiryLabel => Expires is { } expiry && expiry is >0 and <=253402300799
        ?"До "+DateTimeOffset.FromUnixTimeSeconds(expiry).ToLocalTime().ToString("dd.MM.yyyy"):"Срок не указан";
    internal double UsagePercent => Total is >0?Math.Clamp((double)(((decimal)(Upload??0)+(Download??0))/Total.Value*100),0,100):0;
    private static string Bytes(decimal bytes)
    {
        string[] units=["Б","КБ","МБ","ГБ","ТБ","ПБ","ЭБ"];
        var index=0; while(bytes>=1024 && index<units.Length-1) { bytes/=1024; index++; }
        return bytes.ToString("0.#",CultureInfo.CurrentCulture)+" "+units[index];
    }
}
