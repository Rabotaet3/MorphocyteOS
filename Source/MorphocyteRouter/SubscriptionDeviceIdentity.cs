using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MorphocyteRouter;

// New Windows installs share this computer's identity with compatible clients.
// Existing identities are retained until an explicit migration. Never export
// the identity with profiles or replace a damaged identity automatically.
internal static class SubscriptionDeviceIdentity
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MorphocyteOS.SubscriptionDevice.1");
    private static readonly object Gate = new();

    internal static string? ReadWindowsIdentity()
    {
        if(!OperatingSystem.IsWindows()) return null;
        try
        {
            using var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var key=registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value=key?.GetValue("MachineGuid") as string;
            return Valid(value)?value:null;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool Valid(string? value)=>Guid.TryParseExact(value,"D",out var guid)&&guid!=Guid.Empty;
    private static byte[] Protect(string identity)=>ProtectedData.Protect(Encoding.UTF8.GetBytes(identity),Entropy,DataProtectionScope.CurrentUser);
    private static string Read(string path)
    {
        if(new FileInfo(path).Length>4096) throw new InvalidDataException();
        var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.CurrentUser);
        try
        {
            var id=Encoding.UTF8.GetString(bytes);
            if(!Valid(id)) throw new InvalidDataException();
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string GetOrCreate(string directory,Func<string?>? windowsIdentity=null)
    {
        lock (Gate)
        {
            var path = Path.Combine(directory, "subscription-device-id.dat");
            try
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(path))
                {
                    var computer=(windowsIdentity??ReadWindowsIdentity)();
                    var id=Valid(computer)?computer!:Guid.NewGuid().ToString("D");
                    var protectedBytes=Protect(id);
                    // CreateNew cannot replace an identity made by another process.
                    try
                    {
                        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        output.Write(protectedBytes); output.Flush(true);
                    }
                    catch (IOException) when (File.Exists(path)) { }
                }
                return Read(path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
            { throw new InvalidDataException("Не удалось прочитать или сохранить HWID MorphocyteOS. Новый ID автоматически не создаётся; проверь доступ к локальным данным."); }
        }
    }

    internal static bool UseWindowsIdentity(string directory,string expectedIdentity,Func<string?>? windowsIdentity=null)
    {
        lock(Gate)
        {
            var computer=(windowsIdentity??ReadWindowsIdentity)();
            if(!Valid(computer)) throw new InvalidDataException("ID компьютера недоступен. Сохранённый ID не изменён.");
            var current=GetOrCreate(directory,()=>computer);
            if(current!=expectedIdentity) throw new InvalidDataException("ID уже изменился. Открой настройки заново.");
            if(current==computer) return false;
            var path=Path.Combine(directory,"subscription-device-id.dat");
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            var backup=path+".before-windows-"+Guid.NewGuid().ToString("N")+".bak";
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { output.Write(Protect(computer!)); output.Flush(true); }
                File.Replace(temp,path,backup);
                return true;
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
}
