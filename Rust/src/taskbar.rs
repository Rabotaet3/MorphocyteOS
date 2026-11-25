use std::{
    ffi::{OsStr, OsString}, fs, io,
    os::windows::{ffi::{OsStrExt, OsStringExt}, fs::MetadataExt},
    path::{Path, PathBuf},
    sync::mpsc::{self, Sender},
    thread::{self, JoinHandle},
};
use windows::{
    Win32::{
        Foundation::{HWND, PROPERTYKEY},
        Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT,
        System::{
            Com::{CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoUninitialize, IPersistFile, STGM_READ, STGM_READWRITE,
                StructuredStorage::{PROPVARIANT, PVCHF_DEFAULT, PropVariantChangeType}},
            Environment::ExpandEnvironmentStringsW,
            Variant::VT_LPWSTR,
        },
        UI::Shell::{IShellLinkW, SHCNE_UPDATEDIR,
            SHCNE_UPDATEITEM, SHCNF_FLUSHNOWAIT, SHCNF_PATHW, SHChangeNotify, SHFILEINFOW,
            SHGFI_SYSICONINDEX, SHGetFileInfoW, SHUpdateImageW, SLGP_RAWPATH, ShellLink,
            PropertiesSystem::{IPropertyStore, SHGetPropertyStoreForWindow}},
    },
    core::{BSTR, GUID, Interface, PCWSTR},
};

const APP_MODEL: GUID = GUID::from_u128(0x9f4c2855_9f79_4b39_a8d0_e1d42de1d5f3);
const APP_ID: PROPERTYKEY = app_property(5);
const RELAUNCH: PROPERTYKEY = app_property(2);
const ICON: PROPERTYKEY = app_property(3);
const DISPLAY_NAME: PROPERTYKEY = app_property(4);

const fn app_property(pid: u32) -> PROPERTYKEY { PROPERTYKEY { fmtid: APP_MODEL, pid } }

struct Apartment;

impl Apartment {
    fn open() -> windows::core::Result<Self> {
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }.ok()?;
        Ok(Self)
    }
}

impl Drop for Apartment {
    fn drop(&mut self) { unsafe { CoUninitialize() }; }
}

pub(super) struct Taskbar {
    target: PathBuf,
    id: String,
    pending: Option<Sender<u16>>,
    worker: Option<JoinHandle<()>>,
}

impl Taskbar {
    pub(super) fn new() -> io::Result<Self> {
        let target = std::env::current_exe()?;
        let id = application_id(&target);
        let (send, receive) = mpsc::channel();
        let executable = target.clone();
        let app_id = id.clone();
        let worker = thread::Builder::new().name("taskbar-icons".into()).spawn(move || {
            let Ok(_apartment) = Apartment::open() else { return; };
            while let Ok(first) = receive.recv() {
                let resource = receive.try_iter().last().unwrap_or(first);
                if let Some(root) = pins_root() { update_pins(&root, &executable, &app_id, resource); }
            }
        })?;
        Ok(Self { target, id, pending: Some(send), worker: Some(worker) })
    }

    pub(super) fn apply(&self, window: HWND, theme: u16) {
        let resource = crate::theme_icon::theme_icon_id(theme);
        if let Err(error) = self.apply_window(window, resource) {
            crate::trace(&format!("taskbar window: {error}"));
        }
        if let Some(worker) = &self.pending { let _ = worker.send(resource); }
    }

    fn apply_window(&self, window: HWND, resource: u16) -> windows::core::Result<()> {
        let _apartment = Apartment::open()?;
        let target = self.target.to_string_lossy();
        unsafe {
            let store: IPropertyStore = SHGetPropertyStoreForWindow(window)?;
            set_string(&store, &RELAUNCH, &format!("\"{target}\""))?;
            set_string(&store, &DISPLAY_NAME, "Морфоцит OS")?;
            set_string(&store, &ICON, &format!("{target},-{resource}"))?;
            set_string(&store, &APP_ID, &self.id)?;
            store.Commit()
        }
    }
}

impl Drop for Taskbar {
    fn drop(&mut self) {
        self.pending.take();
        if let Some(worker) = self.worker.take() { let _ = worker.join(); }
    }
}

fn application_id(target: &Path) -> String {
    // Keep the ID stable across themes and updates in the same folder.
    let hash = target.to_string_lossy().to_lowercase().bytes().fold(0xcbf29ce484222325u64,
        |hash, byte| (hash ^ u64::from(byte)).wrapping_mul(0x100000001b3));
    format!("MorphocyteOS.Desktop.{hash:016x}")
}

fn set_string(store: &IPropertyStore, key: &PROPERTYKEY, text: &str) -> windows::core::Result<()> {
    let source = PROPVARIANT::from(text);
    let mut value = PROPVARIANT::default();
    unsafe {
        PropVariantChangeType(&mut value, &source, PVCHF_DEFAULT, VT_LPWSTR)?;
        store.SetValue(key, &value)
    }
}

pub(super) fn clear_window(window: HWND) {
    let Ok(_apartment) = Apartment::open() else { return; };
    unsafe {
        if let Ok(store) = SHGetPropertyStoreForWindow::<IPropertyStore>(window) {
            let empty = PROPVARIANT::default();
            for key in [APP_ID, RELAUNCH, ICON, DISPLAY_NAME] { let _ = store.SetValue(&key, &empty); }
        }
    }
}

fn wide(text: &OsStr) -> Vec<u16> { text.encode_wide().chain(Some(0)).collect() }

fn from_buffer(text: &[u16]) -> OsString {
    let length = text.iter().position(|unit| *unit == 0).unwrap_or(text.len());
    OsString::from_wide(&text[..length])
}

fn plain_metadata(path: &Path) -> Option<fs::Metadata> {
    fs::symlink_metadata(path).ok().filter(|meta| meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT.0 == 0)
}

fn plain_directory(path: &Path) -> bool { plain_metadata(path).is_some_and(|meta| meta.is_dir()) }

fn plain_shortcut(path: &Path) -> bool {
    path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("lnk"))
        && plain_metadata(path).is_some_and(|meta| meta.is_file() && meta.len() <= 1024 * 1024)
}

fn pins_root() -> Option<PathBuf> {
    #[cfg(feature = "isolated-check")]
    return std::env::var_os("ATLAS_CHECK_PINS").map(PathBuf::from);
    #[cfg(not(feature = "isolated-check"))]
    unsafe {
        use windows::Win32::{System::Com::CoTaskMemFree,
            UI::Shell::{FOLDERID_RoamingAppData, KNOWN_FOLDER_FLAG, SHGetKnownFolderPath}};
        let raw = SHGetKnownFolderPath(&FOLDERID_RoamingAppData, KNOWN_FOLDER_FLAG(0), None).ok()?;
        let mut directory = PathBuf::from(OsString::from_wide(raw.as_wide()));
        CoTaskMemFree(Some(raw.0.cast()));
        if !plain_directory(&directory) { return None; }
        for component in ["Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"] {
            directory.push(component);
            if !plain_directory(&directory) { return None; }
        }
        Some(directory)
    }
}

fn update_pins(root: &Path, target: &Path, id: &str, resource: u16) {
    if !plain_directory(root) { return; }
    let implicit = root.join("ImplicitAppShortcuts");
    let mut directories = vec![root.join("TaskBar")];
    if plain_directory(&implicit) {
        if let Ok(children) = fs::read_dir(&implicit) {
            directories.extend(children.flatten().map(|entry| entry.path()).filter(|path| plain_directory(path)));
        }
        directories.push(implicit);
    }
    for directory in directories.into_iter().filter(|directory| plain_directory(directory)) {
        if let Ok(entries) = fs::read_dir(directory) {
            for entry in entries.flatten() {
                let path = entry.path();
                if plain_shortcut(&path) {
                    if let Err(error) = update_shortcut(&path, target, id, resource) {
                        crate::trace(&format!("taskbar shortcut: {error}"));
                    }
                }
            }
        }
    }
}

fn same_target(path: &OsStr, target: &Path) -> bool {
    let original = wide(path);
    let length = unsafe { ExpandEnvironmentStringsW(PCWSTR(original.as_ptr()), None) };
    if length == 0 || length > 32768 { return false; }
    let mut expanded = vec![0; length as usize];
    let copied = unsafe { ExpandEnvironmentStringsW(PCWSTR(original.as_ptr()), Some(&mut expanded)) };
    if copied == 0 || copied > length { return false; }
    let expanded = PathBuf::from(from_buffer(&expanded));
    if !expanded.is_absolute() { return false; }
    let Ok(path) = std::path::absolute(&expanded) else { return false; };
    let left = path.to_string_lossy();
    let right = target.to_string_lossy();
    left.trim_start_matches(r"\\?\").to_lowercase() == right.trim_start_matches(r"\\?\").to_lowercase()
}

fn update_shortcut(path: &Path, target: &Path, id: &str, resource: u16) -> Result<(), Box<dyn std::error::Error>> {
    let location = wide(path.as_os_str());
    let executable = wide(target.as_os_str());
    let index = -i32::from(resource);
    let original = fs::read(path)?;
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)?;
        let file = link.cast::<IPersistFile>()?;
        file.Load(PCWSTR(location.as_ptr()), STGM_READ)?;
        let mut buffer = vec![0; 32768];
        // Read the shortcut's recorded target without asking Explorer to resolve it.
        link.GetPath(&mut buffer, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32)?;
        if !same_target(&from_buffer(&buffer), target) { return Ok(()); }
        buffer.fill(0);
        let mut previous_index = 0;
        link.GetIconLocation(&mut buffer, &mut previous_index)?;
        let previous_icon = from_buffer(&buffer);
        let store = link.cast::<IPropertyStore>()?;
        let previous_id = store.GetValue(&APP_ID).ok().and_then(|value| BSTR::try_from(&value).ok());
        let own_icon = same_target(&previous_icon, target);
        if own_icon && previous_index == index && previous_id.is_some_and(|value| value.to_string() == id) {
            return Ok(());
        }
        drop(store);
        let mut shell_info = SHFILEINFOW::default();
        let image_list = SHGetFileInfoW(PCWSTR(location.as_ptr()), Default::default(), Some(&mut shell_info),
            size_of::<SHFILEINFOW>() as u32, SHGFI_SYSICONINDEX);
        if !plain_shortcut(path) || fs::read(path)? != original { return Ok(()); }
        file.Load(PCWSTR(location.as_ptr()), STGM_READWRITE)?;
        buffer.fill(0);
        link.GetPath(&mut buffer, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32)?;
        if !same_target(&from_buffer(&buffer), target) { return Ok(()); }
        link.SetIconLocation(PCWSTR(executable.as_ptr()), index)?;
        let store = link.cast::<IPropertyStore>()?;
        set_string(&store, &APP_ID, id)?;
        store.Commit()?;

        let mut random = [0u8; 16];
        getrandom::fill(&mut random).map_err(|_| io::Error::other("Could not allocate a temporary shortcut name"))?;
        let temporary = path.with_file_name(format!(".morphocyte-{:032x}.tmp", u128::from_le_bytes(random)));
        fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
        let updated = (|| -> Result<bool, Box<dyn std::error::Error>> {
            file.Save(PCWSTR(wide(temporary.as_os_str()).as_ptr()), false)?;
            if !path.parent().is_some_and(plain_directory) || !plain_shortcut(path) || fs::read(path)? != original {
                return Ok(false);
            }
            fs::rename(&temporary, path)?;
            Ok(true)
        })();
        if !matches!(updated, Ok(true)) { let _ = fs::remove_file(&temporary); }
        if updated? {
            if image_list != 0 && (own_icon || previous_icon.is_empty()) {
                SHUpdateImageW(PCWSTR(executable.as_ptr()), previous_index, 0, shell_info.iIcon);
            }
            let flags = SHCNF_PATHW | SHCNF_FLUSHNOWAIT;
            SHChangeNotify(SHCNE_UPDATEITEM, flags, Some(location.as_ptr().cast()), None);
            if let Some(dir) = path.parent() {
                SHChangeNotify(SHCNE_UPDATEDIR, flags, Some(wide(dir.as_os_str()).as_ptr().cast()), None);
            }
        }
    }
    Ok(())
}
