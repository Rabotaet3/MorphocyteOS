using System.IO;
using System.Security.Cryptography;
using System.Text;
using MorphocyteRouter;

internal static class SubscriptionIdentityChecks
{
    internal static void Run(Action<bool,string> check)
    {
        const string firstComputer="aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        const string secondComputer="11111111-2222-4333-8444-555555555555";
        var root=Path.Combine(Path.GetTempPath(),"MorphocyteOS-identity-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string FilePath(string folder)=>Path.Combine(folder,"subscription-device-id.dat");
        string Decode(string path)=>Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),Encoding.UTF8.GetBytes("MorphocyteOS.SubscriptionDevice.1"),DataProtectionScope.CurrentUser));
        void Reject(Action action,string text) {
            try { action(); } catch(InvalidDataException) { check(true,text); return; }
            throw new Exception("FAIL: "+text);
        }
        try
        {
            var first=Path.Combine(root,"first"); var second=Path.Combine(root,"second"); var other=Path.Combine(root,"other-computer");
            check(SubscriptionDeviceIdentity.GetOrCreate(first,()=>firstComputer)==firstComputer
                &&SubscriptionDeviceIdentity.GetOrCreate(second,()=>firstComputer)==firstComputer,"separate new installations on one computer reuse its device identity instead of consuming distinct slots");
            check(SubscriptionDeviceIdentity.GetOrCreate(other,()=>secondComputer)==secondComputer,"another computer receives its own identity rather than a value embedded in the build");
            check(Decode(FilePath(first))==firstComputer&&!Encoding.UTF8.GetString(File.ReadAllBytes(FilePath(first))).Contains(firstComputer),"the shared computer identity is still protected with Windows user encryption");
            var queried=false;
            check(SubscriptionDeviceIdentity.GetOrCreate(first,()=> { queried=true; return secondComputer; })==firstComputer&&!queried,"existing identities survive system changes without reading or replacing them automatically");
            var legacy=Path.Combine(root,"legacy");
            var original=SubscriptionDeviceIdentity.GetOrCreate(legacy,()=>null);
            check(Guid.TryParseExact(original,"D",out _)&&SubscriptionDeviceIdentity.GetOrCreate(legacy,()=>firstComputer)==original,"unavailable computer identity falls back once to a stable saved ID, without a later automatic migration");
            Reject(()=>SubscriptionDeviceIdentity.UseWindowsIdentity(legacy,secondComputer,()=>firstComputer),"a stale migration prompt cannot replace an identity changed by another operation");
            Reject(()=>SubscriptionDeviceIdentity.UseWindowsIdentity(legacy,original,()=>null),"migration fails without changing the old identity when Windows ID is unavailable");
            check(SubscriptionDeviceIdentity.UseWindowsIdentity(legacy,original,()=>firstComputer)&&Decode(FilePath(legacy))==firstComputer,"explicit migration atomically saves this computer's identity");
            var backup=Directory.GetFiles(legacy,"subscription-device-id.dat.before-windows-*.bak").Single();
            check(Decode(backup)==original,"explicit migration retains the previous encrypted identity for recovery");
            check(!SubscriptionDeviceIdentity.UseWindowsIdentity(legacy,firstComputer,()=>firstComputer)
                &&Directory.GetFiles(legacy,"*.bak").Length==1,"repeating the migration does not rotate the ID or accumulate extra backup files");
            File.WriteAllBytes(FilePath(legacy),new byte[]{1,2,3});
            Reject(()=>SubscriptionDeviceIdentity.GetOrCreate(legacy,()=>firstComputer),"a damaged saved ID is never replaced with a Windows ID automatically");
            Reject(()=>SubscriptionDeviceIdentity.UseWindowsIdentity(legacy,firstComputer,()=>firstComputer),"explicit migration also refuses to overwrite a damaged saved ID");
            check(File.ReadAllBytes(FilePath(legacy)).SequenceEqual(new byte[]{1,2,3})&&Decode(backup)==original,"failed recovery preserves both damaged data and the encrypted backup");
        }
        finally { Directory.Delete(root,true); }
    }
}
