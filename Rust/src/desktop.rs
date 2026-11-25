use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::process::Child;
use windows::{
    Win32::{
        Foundation::{HANDLE, LPARAM, WAIT_ABANDONED, WAIT_OBJECT_0, WAIT_TIMEOUT, WPARAM},
        System::{
            JobObjects::{
                AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
                SetInformationJobObject,
            },
            Threading::{CreateMutexW, ReleaseMutex, WaitForSingleObject},
        },
        UI::WindowsAndMessaging::{
            FindWindowW, FlashWindow, IsIconic, IsWindowVisible, PostMessageW, SW_RESTORE, SW_SHOW, SetForegroundWindow, ShowWindowAsync, WM_APP,
        },
    },
    core::{PCWSTR, w},
};

pub const WINDOW_TITLE: &str = "Морфоцит OS";
pub const WINDOW_CLASS: &str = "MorphocyteOS.Atlas.MainWindow";

pub struct Instance(OwnedHandle);

impl Instance {
    pub fn acquire(timeout_millis: u32) -> std::io::Result<Option<Self>> {
        let name = if cfg!(feature = "isolated-check") {
            w!(r"Local\MorphocyteAtlas.Desktop.Verification")
        } else {
            w!(r"Local\MorphocyteAtlas.Desktop")
        };
        let mutex = unsafe { CreateMutexW(None, false, name) };
        let handle = match mutex {
            Ok(handle) => unsafe { OwnedHandle::from_raw_handle(handle.0) },
            // A higher-integrity copy may already own the named mutex.
            Err(error) if error.code().0 as u32 == 0x80070005 => return Ok(None),
            Err(error) => return Err(std::io::Error::other(error)),
        };
        match unsafe { WaitForSingleObject(HANDLE(handle.as_raw_handle()), timeout_millis) } {
            WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Some(Self(handle))),
            WAIT_TIMEOUT => Ok(None),
            _ => Err(std::io::Error::last_os_error()),
        }
    }
}

impl Drop for Instance {
    fn drop(&mut self) {
        let _ = unsafe { ReleaseMutex(HANDLE(self.0.as_raw_handle())) };
    }
}

pub fn activate_existing() -> bool {
    let class: Vec<u16> = WINDOW_CLASS.encode_utf16().chain(Some(0)).collect();
    let window = unsafe { FindWindowW(PCWSTR(class.as_ptr()), PCWSTR::null()) };
    if let Ok(window) = window
        && unsafe { PostMessageW(Some(window), WM_APP + 71, WPARAM(1), LPARAM(0)) }.is_ok() {
        return true;
    }
    let Ok(window) = window.or_else(|_| unsafe { FindWindowW(PCWSTR::null(), w!("Морфоцит OS · Атлас")) }) else {
        return false;
    };
    unsafe {
        if IsIconic(window).as_bool() {
            let _ = ShowWindowAsync(window, SW_RESTORE);
        } else if !IsWindowVisible(window).as_bool() {
            let _ = ShowWindowAsync(window, SW_SHOW);
        }
        if !SetForegroundWindow(window).as_bool() {
            let _ = FlashWindow(window, true);
        }
    }
    true
}

pub(crate) fn attach_backend(child: &Child) -> std::io::Result<OwnedHandle> {
    unsafe {
        let handle = CreateJobObjectW(None, PCWSTR::null()).map_err(std::io::Error::other)?;
        let job = OwnedHandle::from_raw_handle(handle.0);
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(
            handle,
            JobObjectExtendedLimitInformation,
            std::ptr::from_ref(&limits).cast(),
            std::mem::size_of_val(&limits) as u32,
        )
        .map_err(std::io::Error::other)?;
        AssignProcessToJobObject(handle, HANDLE(child.as_raw_handle()))
            .map_err(std::io::Error::other)?;
        Ok(job)
    }
}
