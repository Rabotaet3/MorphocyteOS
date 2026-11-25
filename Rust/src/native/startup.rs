use super::{Result, problem};
use std::{ffi::OsString, os::windows::{ffi::{OsStrExt, OsStringExt}, io::{FromRawHandle, OwnedHandle}}, path::PathBuf};
use windows::{
    Win32::{
        Foundation::{ERROR_FILE_NOT_FOUND, ERROR_INSUFFICIENT_BUFFER, HANDLE, HLOCAL, LocalFree, VARIANT_FALSE, VARIANT_TRUE},
        Security::{GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser, Authorization::ConvertSidToStringSidW},
        System::{
            Com::{CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize},
            SystemInformation::GetSystemDirectoryW,
            TaskScheduler::{IExecAction, ILogonTrigger, IRegisteredTask, ITaskFolder, ITaskService, TASK_ACTION_EXEC,
                TASK_CREATE_OR_UPDATE, TASK_INSTANCES_IGNORE_NEW, TASK_LOGON_INTERACTIVE_TOKEN, TASK_RUNLEVEL_LUA,
                TASK_TRIGGER_LOGON, TASK_VALIDATE_ONLY, TaskScheduler},
            Threading::{GetCurrentProcess, OpenProcessToken},
            Variant::VARIANT,
        },
    },
    core::{BSTR, Interface, PWSTR},
};

struct Com;

impl Com {
    fn initialize() -> windows::core::Result<Self> {
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.ok()?;
        Ok(Self)
    }
}

impl Drop for Com {
    fn drop(&mut self) { unsafe { CoUninitialize() }; }
}

fn user_id() -> Result<BSTR> {
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)?;
        let _token = OwnedHandle::from_raw_handle(token.0);
        let mut bytes = 0;
        if let Err(error) = GetTokenInformation(token, TokenUser, None, 0, &mut bytes)
            && error.code() != ERROR_INSUFFICIENT_BUFFER.to_hresult() { return Err(error.into()); }
        let mut buffer = vec![0usize; (bytes as usize).div_ceil(size_of::<usize>())];
        GetTokenInformation(token, TokenUser, Some(buffer.as_mut_ptr().cast()), bytes, &mut bytes)?;
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        let mut sid = PWSTR::null();
        ConvertSidToStringSidW(user.User.Sid, &mut sid)?;
        let value = BSTR::from_wide(sid.as_wide());
        let _ = LocalFree(Some(HLOCAL(sid.0.cast())));
        Ok(value)
    }
}

struct Task {
    service: ITaskService,
    folder: ITaskFolder,
    name: BSTR,
    user: BSTR,
    launcher: BSTR,
    arguments: BSTR,
}

impl Task {
    fn connect() -> Result<Self> {
        let user = user_id()?;
        let name = BSTR::from(format!("MorphocyteOS-Rust-{user}"));
        let mut directory = [0u16; 260];
        let length = unsafe { GetSystemDirectoryW(Some(&mut directory)) } as usize;
        if length == 0 || length >= directory.len() { return Err(problem("Не найдена системная папка Windows.")); }
        let launcher = PathBuf::from(OsString::from_wide(&directory[..length]))
            .join(r"WindowsPowerShell\v1.0\powershell.exe");
        let launcher = BSTR::from_wide(&launcher.as_os_str().encode_wide().collect::<Vec<_>>());
        let exe = std::env::current_exe()?;
        let exe = exe.to_str().ok_or_else(|| problem("Путь к Морфоциту содержит неподдерживаемые символы."))?;
        // Use a PowerShell single-quoted literal. Double all characters it treats as apostrophes.
        let mut path = String::new();
        for ch in exe.chars() {
            if matches!(ch, '\'' | '\u{2018}' | '\u{2019}' | '\u{201a}' | '\u{201b}') { path.push(ch); }
            path.push(ch);
        }
        let arguments = BSTR::from(format!(r#"-NoProfile -WindowStyle Hidden -NonInteractive -Command "Start-Process -FilePath '{path}' -Verb RunAs -ArgumentList '--startup'""#));
        let empty = VARIANT::default();
        unsafe {
            let service: ITaskService = CoCreateInstance(&TaskScheduler, None, CLSCTX_INPROC_SERVER)?;
            service.Connect(&empty, &empty, &empty, &empty)?;
            let folder = service.GetFolder(&BSTR::from("\\"))?;
            Ok(Self { service, folder, name, user, launcher, arguments })
        }
    }

    fn current(&self) -> Result<Option<IRegisteredTask>> {
        let registered = match unsafe { self.folder.GetTask(&self.name) } {
            Ok(task) => task,
            Err(error) if error.code() == ERROR_FILE_NOT_FOUND.to_hresult() => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        unsafe {
            let actions = registered.Definition()?.Actions()?;
            let mut count = 0;
            actions.Count(&mut count)?;
            if count != 1 { return Ok(None); }
            let Ok(action) = actions.get_Item(1)?.cast::<IExecAction>() else { return Ok(None); };
            let mut arguments = BSTR::new();
            let mut path = BSTR::new();
            action.Arguments(&mut arguments)?;
            action.Path(&mut path)?;
            Ok((arguments == self.arguments && path == self.launcher).then_some(registered))
        }
    }

    fn register(&self) -> Result<()> {
        unsafe {
            let task = self.service.NewTask(0)?;
            task.RegistrationInfo()?.SetDescription(&BSTR::from("Запуск Морфоцита при входе пользователя Windows."))?;
            let principal = task.Principal()?;
            principal.SetUserId(&self.user)?;
            principal.SetLogonType(TASK_LOGON_INTERACTIVE_TOKEN)?;
            // The launcher runs as the signed-in user. Windows grants elevation to the app through UAC.
            principal.SetRunLevel(TASK_RUNLEVEL_LUA)?;
            let settings = task.Settings()?;
            settings.SetMultipleInstances(TASK_INSTANCES_IGNORE_NEW)?;
            settings.SetDisallowStartIfOnBatteries(VARIANT_FALSE)?;
            settings.SetStopIfGoingOnBatteries(VARIANT_FALSE)?;
            settings.SetStartWhenAvailable(VARIANT_TRUE)?;
            let trigger = task.Triggers()?.Create(TASK_TRIGGER_LOGON)?.cast::<ILogonTrigger>()?;
            trigger.SetUserId(&self.user)?;
            let action = task.Actions()?.Create(TASK_ACTION_EXEC)?.cast::<IExecAction>()?;
            action.SetPath(&self.launcher)?;
            action.SetArguments(&self.arguments)?;
            let empty = VARIANT::default();
            let flags = if cfg!(feature = "isolated-check") { TASK_VALIDATE_ONLY } else { TASK_CREATE_OR_UPDATE };
            let registered = self.folder.RegisterTaskDefinition(&self.name, &task, flags.0,
                &VARIANT::from(self.user.clone()), &empty, TASK_LOGON_INTERACTIVE_TOKEN, &empty);
            if let Err(error) = registered {
                // Validation returns S_OK and no task object. The bindings report an empty error for a null object.
                if flags != TASK_VALIDATE_ONLY || error.code().0 != 0 { return Err(error.into()); }
            }
        }
        Ok(())
    }
}

pub(super) fn enabled() -> Result<bool> {
    let _com = Com::initialize()?;
    let task = Task::connect()?;
    Ok(match task.current()? {
        Some(current) => unsafe { current.Enabled()? }.as_bool(),
        None => false,
    })
}

pub(super) fn set_enabled(enabled: bool) -> Result<()> {
    let _com = Com::initialize()?;
    let task = Task::connect()?;
    if enabled { task.register()?; }
    else if !cfg!(feature = "isolated-check") && task.current()?.is_some() {
        unsafe { task.folder.DeleteTask(&task.name, 0) }?;
    }
    Ok(())
}
