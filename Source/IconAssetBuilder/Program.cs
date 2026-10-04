using System.IO;
using System.Windows.Media.Imaging;
using MorphocyteRouter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected PNG input and ICO output paths.");
        var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(Path.GetFullPath(args[0]));
        image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
        File.WriteAllBytes(args[1], IconArtwork.EncodeIcon(IconArtwork.TrimPadding(image)));
        Console.WriteLine("High-quality ICO created: " + string.Join(", ", IconArtwork.Sizes));
    }
}
