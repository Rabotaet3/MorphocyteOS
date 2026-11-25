use windows::Win32::UI::WindowsAndMessaging::{MSG, WM_APP};

#[cfg(feature = "native-router")]
mod menu;

pub enum Action {
    Restore,
    Power,
    Settings,
    Journal,
    Exit,
    Theme(u16),
}

pub fn action(message: &MSG) -> Option<Action> {
    if message.message != WM_APP + 71 {
        return None;
    }
    match message.wParam.0 {
        1 => Some(Action::Restore),
        2 => Some(Action::Power),
        3 => Some(Action::Settings),
        4 => Some(Action::Journal),
        5 => Some(Action::Exit),
        id @ 101..=109 => Some(Action::Theme((id - 100) as u16)),
        _ => None,
    }
}

#[cfg(feature = "native-router")]
pub mod native {
    use super::menu::{self, Popup, Theme};
    use serde_json::Value;
    use std::{
        cell::Cell, ptr, rc::Rc,
        sync::{Arc, mpsc, atomic::{AtomicBool, Ordering}},
        thread, time::Duration,
    };
    use windows::{
        Win32::{
            Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, POINT, WPARAM},
            System::LibraryLoader::GetModuleHandleW,
            UI::{
                HiDpi::GetDpiForWindow,
                Shell::{
                    NIF_ICON, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY,
                    NIM_SETVERSION, NIN_SELECT, NOTIFYICON_VERSION_4, NOTIFYICONDATAW, NOTIFYICONIDENTIFIER,
                    Shell_NotifyIconGetRect, Shell_NotifyIconW,
                },
                WindowsAndMessaging::*,
            },
        },
        core::{PCWSTR, w},
    };

    const CLICK: u32 = WM_APP + 72;
    const SET_THEME: u32 = WM_APP + 73;
    const REFRESH: u32 = WM_APP + 74;
    const ID: u32 = 1;
    const NIN_KEYSELECT: u32 = NIN_SELECT | 1;

    #[derive(Default)]
    struct Status {
        connected: AtomicBool,
        registered: AtomicBool,
    }

    struct Callbacks {
        owner: HWND,
        status: Arc<Status>,
        themes: Rc<[Theme]>,
        resource: Cell<usize>,
        taskbar_created: u32,
        version4: Cell<bool>,
        popup: Cell<HWND>,
    }

    pub struct Tray {
        hwnd: isize,
        status: Arc<Status>,
    }

    impl Tray {
        pub fn start(owner: isize, catalog: &Value, resource: usize) -> Option<Self> {
            let themes = menu::themes(catalog);
            let status = Arc::new(Status::default());
            let worker_status = Arc::clone(&status);
            let (sender, receiver) = mpsc::channel();
            thread::spawn(move || unsafe {
                let Ok(module) = GetModuleHandleW(PCWSTR::null()) else { return; };
                let instance = HINSTANCE(module.0);
                if !menu::register(instance) { return; }
                let name = w!("MorphocyteOS.NativeTray");
                let class = WNDCLASSW { hInstance: instance, lpszClassName: name, lpfnWndProc: Some(message),
                    ..Default::default() };
                if RegisterClassW(&class) == 0 { return; }
                let Ok(hwnd) = CreateWindowExW(WINDOW_EX_STYLE(0), name, name, WINDOW_STYLE(0), 0, 0, 0, 0,
                    None, None, Some(instance), None) else { return; };
                let callbacks = Box::new(Callbacks { owner: HWND(owner as _), status: worker_status,
                    themes: themes.into(), resource: Cell::new(resource),
                    taskbar_created: RegisterWindowMessageW(w!("TaskbarCreated")),
                    version4: Cell::new(false), popup: Cell::new(HWND::default()) });
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, ptr::from_ref(callbacks.as_ref()) as isize);
                // Explorer sends tray notifications from its unelevated process.
                let _ = ChangeWindowMessageFilterEx(hwnd, CLICK, MSGFLT_ALLOW, None);
                if callbacks.taskbar_created != 0 {
                    let _ = ChangeWindowMessageFilterEx(hwnd, callbacks.taskbar_created, MSGFLT_ALLOW, None);
                }
                callbacks.register(hwnd);
                if sender.send(hwnd.0 as isize).is_err() {
                    let _ = DestroyWindow(hwnd);
                    return;
                }
                let mut event = MSG::default();
                while GetMessageW(&mut event, None, 0, 0).0 > 0 {
                    let _ = TranslateMessage(&event);
                    DispatchMessageW(&event);
                }
            });
            receiver.recv_timeout(Duration::from_secs(1)).ok().map(|hwnd| Self { hwnd, status })
        }

        pub fn available(&self) -> bool { self.status.registered.load(Ordering::Acquire) }

        pub fn set_connected(&self, connected: bool) {
            if self.status.connected.swap(connected, Ordering::AcqRel) != connected {
                self.post(REFRESH, 0);
            }
        }

        pub fn set_theme(&self, resource: usize) { self.post(SET_THEME, resource); }

        fn post(&self, message: u32, value: usize) {
            let _ = unsafe { PostMessageW(Some(HWND(self.hwnd as _)), message, WPARAM(value), LPARAM(0)) };
        }
    }

    impl Drop for Tray {
        fn drop(&mut self) { self.post(WM_CLOSE, 0); }
    }

    impl Callbacks {
        fn connected(&self) -> bool { self.status.connected.load(Ordering::Acquire) }

        fn icon_data(&self, hwnd: HWND) -> NOTIFYICONDATAW {
            let mut data = NOTIFYICONDATAW {
                cbSize: size_of::<NOTIFYICONDATAW>() as u32, hWnd: hwnd, uID: ID,
                uFlags: NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP, uCallbackMessage: CLICK,
                ..Default::default()
            };
            let resource = crate::theme_icon::theme_icon_id(self.resource.get() as u16);
            let resource = PCWSTR(resource as usize as *const u16);
            unsafe {
                if let Ok(module) = GetModuleHandleW(PCWSTR::null()) {
                    data.hIcon = LoadIconW(Some(HINSTANCE(module.0)), resource).unwrap_or_default();
                }
            }
            let tip = if self.connected() { "Морфоцит OS · VPN включён" } else { "Морфоцит OS · VPN выключен" };
            for (slot, character) in data.szTip.iter_mut().zip(tip.encode_utf16()) { *slot = character; }
            data
        }

        fn register(&self, hwnd: HWND) {
            let mut data = self.icon_data(hwnd);
            let registered = unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.as_bool();
            self.status.registered.store(registered, Ordering::Release);
            data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
            self.version4.set(registered && unsafe { Shell_NotifyIconW(NIM_SETVERSION, &data) }.as_bool());
        }

        fn dispatch(&self, id: usize) {
            let _ = unsafe { PostMessageW(Some(self.owner), WM_APP + 71, WPARAM(id), LPARAM(0)) };
        }

        fn clicked(&self, hwnd: HWND, param: WPARAM, detail: LPARAM) {
            let signal = detail.0 as u32;
            let icon = signal >> 16;
            if icon != 0 && icon != ID { return; }
            let event = signal & 0xffff;
            match event {
                NIN_SELECT | NIN_KEYSELECT | WM_LBUTTONDBLCLK => self.dispatch(1),
                WM_CONTEXTMENU => {
                    let point = POINT { x: param.0 as i16 as i32, y: (param.0 >> 16) as i16 as i32 };
                    self.show_menu(hwnd, (point.x != -1 || point.y != -1).then_some(point));
                }
                WM_RBUTTONUP if !self.version4.get() => self.show_menu(hwnd, None),
                _ => {}
            }
        }

        fn show_menu(&self, hwnd: HWND, point: Option<POINT>) {
            let existing = self.popup.get();
            if unsafe { IsWindow(Some(existing)) }.as_bool() {
                let _ = unsafe { DestroyWindow(existing) };
                return;
            }
            let point = point.or_else(|| {
                let identifier = NOTIFYICONIDENTIFIER { cbSize: size_of::<NOTIFYICONIDENTIFIER>() as u32,
                    hWnd: hwnd, uID: ID, ..Default::default() };
                let bounds = unsafe { Shell_NotifyIconGetRect(&identifier) }.ok()?;
                Some(POINT { x: (bounds.left + bounds.right) / 2, y: bounds.top })
            }).or_else(|| {
                let mut cursor = POINT::default();
                unsafe { GetCursorPos(&mut cursor).ok()?; }
                Some(cursor)
            });
            let Some(point) = point else { return; };
            unsafe {
                let _ = SetWindowPos(hwnd, None, point.x, point.y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
                match Popup::show(hwnd, self.owner, Rc::clone(&self.themes), self.resource.get(), self.connected(), point, GetDpiForWindow(hwnd)) {
                    Ok(popup) => self.popup.set(popup),
                    Err(error) => crate::trace(&format!("tray menu: {error}")),
                }
            }
        }
    }

    unsafe extern "system" fn message(hwnd: HWND, id: u32, param: WPARAM, detail: LPARAM) -> LRESULT {
        unsafe {
            let Some(state) = (GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Callbacks).as_ref()
                else { return DefWindowProcW(hwnd, id, param, detail); };
            match id {
                CLICK => state.clicked(hwnd, param, detail),
                SET_THEME if (1..=9).contains(&param.0) => {
                    state.resource.set(param.0);
                    let _ = Shell_NotifyIconW(NIM_MODIFY, &state.icon_data(hwnd));
                }
                REFRESH => { let _ = Shell_NotifyIconW(NIM_MODIFY, &state.icon_data(hwnd)); }
                menu::CLOSED => {
                    if state.popup.get().0 as usize == param.0 { state.popup.set(HWND::default()); }
                }
                WM_DESTROY => {
                    let _ = Shell_NotifyIconW(NIM_DELETE, &state.icon_data(hwnd));
                    state.status.registered.store(false, Ordering::Release);
                    PostQuitMessage(0);
                }
                unexpected if unexpected != 0 && unexpected == state.taskbar_created => state.register(hwnd),
                _ => return DefWindowProcW(hwnd, id, param, detail),
            }
            LRESULT(0)
        }
    }
}
