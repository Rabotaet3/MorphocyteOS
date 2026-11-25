use super::Result;
use serde_json::{Value, json};
use std::{collections::{BTreeMap, HashMap}, os::windows::io::{FromRawHandle, OwnedHandle}};
use windows::{core::BOOL, Win32::{
    Foundation::{ERROR_NO_MORE_FILES, HWND, LPARAM},
    System::Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW,
        Process32NextW, TH32CS_SNAPPROCESS},
    UI::WindowsAndMessaging::{EnumWindows, GW_OWNER, GetWindow, GetWindowTextLengthW,
        GetWindowTextW, GetWindowThreadProcessId, IsWindowVisible},
}};

pub(super) fn valid_name(name: &str) -> bool {
    name.len() > 4 && name.len() <= 260 && name.to_ascii_lowercase().ends_with(".exe")
        && !name.contains([',', '/', '\\', ':']) && !name.chars().any(char::is_control)
}

pub(super) fn running_apps() -> Result<Value> {
    let mut names = HashMap::new();
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)?;
        let _handle = OwnedHandle::from_raw_handle(snapshot.0);
        let mut entry = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
        Process32FirstW(snapshot, &mut entry)?;
        loop {
            let length = entry.szExeFile.iter().position(|unit| *unit == 0).unwrap_or(entry.szExeFile.len());
            let name = String::from_utf16_lossy(&entry.szExeFile[..length]);
            if entry.th32ProcessID != std::process::id() && valid_name(&name) { names.insert(entry.th32ProcessID, name); }
            if let Err(error) = Process32NextW(snapshot, &mut entry) {
                if error.code() == ERROR_NO_MORE_FILES.to_hresult() { break; }
                return Err(error.into());
            }
        }
    }
    let mut windows = VisibleApps { names, entries: BTreeMap::new() };
    unsafe { EnumWindows(Some(collect_window), LPARAM((&mut windows as *mut VisibleApps) as isize))?; }
    Ok(json!(windows.entries.into_values().map(|(name, title)| json!({"name":name,"title":title})).collect::<Vec<_>>()))
}

struct VisibleApps {
    names: HashMap<u32, String>,
    entries: BTreeMap<String, (String, String)>,
}

unsafe extern "system" fn collect_window(window: HWND, data: LPARAM) -> BOOL {
    unsafe {
        if !IsWindowVisible(window).as_bool() || GetWindow(window, GW_OWNER).is_ok() { return BOOL(1); }
        let mut pid = 0;
        GetWindowThreadProcessId(window, Some(&mut pid));
        let apps = &mut *(data.0 as *mut VisibleApps);
        let Some(name) = apps.names.get(&pid) else { return BOOL(1); };
        let length = GetWindowTextLengthW(window);
        if length <= 0 { return BOOL(1); }
        let mut buffer = vec![0; length.min(512) as usize + 1];
        let copied = GetWindowTextW(window, &mut buffer).max(0) as usize;
        let title = String::from_utf16_lossy(&buffer[..copied]);
        let title = title.trim();
        if !title.is_empty() {
            apps.entries.entry(name.to_lowercase()).or_insert_with(|| (name.clone(), title.to_owned()));
        }
    }
    BOOL(1)
}
