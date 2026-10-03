using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MorphocyteRouter;

internal static class StartupRegistration
{
    internal static void SetEnabled(bool enabled)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Не удалось определить пользователя Windows.");
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Планировщик Windows недоступен.");
        dynamic scheduler = Activator.CreateInstance(type)!;
        try
        {
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            var name = "MorphocyteOS-" + sid;
            if (!enabled)
            {
                try { folder.DeleteTask(name, 0); }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                return;
            }
            var executable = Environment.ProcessPath ?? throw new IOException("Не найден EXE приложения.");
            if (!Path.GetFileName(executable).Equals("MorphocyteOS.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Автозапуск настраивается из собранного MorphocyteOS.exe.");
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "MorphocyteOS: запуск в трее при входе пользователя в Windows.";
            task.Principal.UserId = sid;
            task.Principal.LogonType = 3; // Interactive token: no password is stored.
            task.Principal.RunLevel = 1;
            task.Settings.MultipleInstances = 2;
            task.Settings.StartWhenAvailable = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S";
            dynamic trigger = task.Triggers.Create(9);
            trigger.UserId = sid;
            dynamic action = task.Actions.Create(0);
            action.Path = executable;
            action.Arguments = "--startup";
            action.WorkingDirectory = Path.GetDirectoryName(executable);
            folder.RegisterTaskDefinition(name, task, 6, sid, null, 3, null);
        }
        catch (COMException ex) { throw new IOException("Не удалось изменить автозапуск в планировщике Windows. Проверь права администратора.", ex); }
        finally { Marshal.FinalReleaseComObject(scheduler); }
    }
}
