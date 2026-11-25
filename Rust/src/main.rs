#![cfg_attr(all(not(debug_assertions), not(test)), windows_subsystem = "windows")]

mod tray;
mod theme_icon;
#[cfg(feature = "native-router")]
mod taskbar;
mod update;
mod window_state;

use morphocyte_atlas::desktop;
#[cfg(not(feature = "native-router"))]
use morphocyte_atlas::bridge::{self, Backend};
#[cfg(feature = "native-router")]
use morphocyte_atlas::native::Backend;
use serde_json::{Value, json};
#[cfg(not(feature = "native-router"))]
use std::fs;
use std::{borrow::Cow, cell::RefCell, os::windows::ffi::OsStrExt, rc::Rc, time::{Duration, Instant}};
use tao::{
    event::{Event, WindowEvent},
    event_loop::{ControlFlow, EventLoopBuilder},
    platform::windows::{EventLoopBuilderExtWindows, IconExtWindows, WindowBuilderExtWindows, WindowExtWindows},
    window::{Icon, Window, WindowBuilder},
};
use wry::{WebViewBuilder, http::Response};
use windows::Win32::{Foundation::HWND, UI::WindowsAndMessaging::{MSG, SW_SHOW, ShowWindow}};

const ATLAS_PAGE: &str = include_str!("../ui/index.html");
const DEFAULT_THEME: &str = "Тёмная морфоцитная";

enum AppEvent {
    Command(Value),
    Reply(Value),
    Tray(tray::Action),
    UpdateReady(std::io::Result<update::Prepared>),
}

fn original_icon(resource: u16) -> Option<Icon> {
    if let Ok(icon) = Icon::from_resource(theme_icon::theme_icon_id(resource), None) { return Some(icon); }
    let bytes = include_bytes!("../ui/icons/default.png");
    let mut decoder = png::Decoder::new(bytes.as_slice());
    decoder.set_transformations(png::Transformations::normalize_to_color8());
    let mut reader = decoder.read_info().ok()?;
    let mut buffer = vec![0; reader.output_buffer_size()];
    let info = reader.next_frame(&mut buffer).ok()?;
    let decoded = &buffer[..info.buffer_size()];
    let rgba = match info.color_type {
        png::ColorType::Rgba => decoded.to_vec(),
        png::ColorType::Rgb => decoded
            .chunks_exact(3)
            .flat_map(|pixel| [pixel[0], pixel[1], pixel[2], 255])
            .collect(),
        _ => return None,
    };
    Icon::from_rgba(rgba, info.width, info.height).ok()
}

#[cfg(not(feature = "native-router"))]
fn saved_theme() -> Option<String> {
    fn read(path: &std::path::Path) -> std::io::Result<Value> {
        let bytes = fs::read(path)?;
        let json = bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes);
        Ok(serde_json::from_slice(json)?)
    }

    let settings = bridge::path().ok()?.with_file_name("settings.json");
    let value = match read(&settings) {
        Ok(value) if value.is_object() => value,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return None,
        _ => read(&settings.with_extension("json.bak")).ok()?,
    };
    value.get("Theme")?.as_str().map(|name| name.trim().to_owned())
}

#[cfg(feature = "native-router")]
fn saved_theme() -> Option<String> { morphocyte_atlas::native::theme() }

fn theme_catalog() -> Value {
    ATLAS_PAGE.split_once("<script type=\"application/json\" id=\"atlas-themes\">")
        .and_then(|(_, page)| page.split_once("</script>"))
        .and_then(|(data, _)| serde_json::from_str::<Value>(data).ok())
        .unwrap_or_default()
}

fn startup_palette(catalog: &Value) -> (Value, wry::RGBA, u16) {
    let chosen = saved_theme().unwrap_or_else(|| DEFAULT_THEME.to_owned()).to_lowercase();
    let theme = catalog.as_object().and_then(|themes| {
        themes.keys().find(|name| name.to_lowercase() == chosen)
    }).map(String::as_str).unwrap_or(DEFAULT_THEME);
    let palette = &catalog[theme];
    let rgb = palette["canvas"].as_str().and_then(|color| color.strip_prefix('#'))
        .and_then(|color| u32::from_str_radix(color, 16).ok()).unwrap_or(0x11_21_1e);
    let color = ((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8, 255);
    (json!({"theme":theme,"background":format!("#{rgb:06x}"),
        "scheme":if palette["light"] == true { "light" } else { "dark" }}), color,
        palette["iconResource"].as_u64().unwrap_or(1) as u16)
}

fn show_initial_window(window: &Window, view: &wry::WebView, startup: &mut bool, tray_available: bool) {
    if *startup && tray_available {
        *startup = false;
    } else {
        window.set_minimized(*startup);
        window.set_visible(true);
        if !*startup { let _ = view.evaluate_script("window.atlasShown?.()"); }
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    #[cfg(feature = "native-router")]
    if let Some(path) = std::env::args_os().nth(1).filter(|arg| arg == "--check-native-profile")
        .and_then(|_| std::env::args_os().nth(2)) {
        let live = std::env::args().any(|arg| arg == "--live");
        let tunnel = std::env::args().any(|arg| arg == "--validate-tun");
        match morphocyte_atlas::native::check_profile(&morphocyte_atlas::native::root()?, std::path::Path::new(&path), live, tunnel) {
            Ok(count) => println!("Loaded {count} servers in Rust. Local engine started: {live}. TUN configuration validated: {tunnel}."),
            Err(error) => {
                trace(&format!("profile check: {error}"));
                eprintln!("{error}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    trace("main");
    let elevation_handoff = std::env::args().any(|arg| arg == "--connect-admin");
    let mut startup = std::env::args().any(|arg| arg == "--startup");
    if !startup && !elevation_handoff && desktop::activate_existing() {
        return Ok(());
    }
    let Some(instance) = desktop::Instance::acquire(if elevation_handoff { 50_000 } else { 0 })? else {
        if !startup { desktop::activate_existing(); }
        return Ok(());
    };
    if update::installing() { return Ok(()); }
    let tray_proxy = Rc::new(RefCell::new(None::<tao::event_loop::EventLoopProxy<AppEvent>>));
    let receiver = Rc::clone(&tray_proxy);
    let mut builder = EventLoopBuilder::<AppEvent>::with_user_event();
    builder.with_msg_hook(move |message| {
        let message = unsafe { &*message.cast::<MSG>() };
        if let Some(notice) = tray::action(message)
            && let Some(proxy) = receiver.borrow().as_ref() {
            let _ = proxy.send_event(AppEvent::Tray(notice));
        }
        false
    });
    let event_loop = builder.build();
    *tray_proxy.borrow_mut() = Some(event_loop.create_proxy());
    trace("event loop");
    let catalog = theme_catalog();
    let (startup_palette, background, icon_resource) = startup_palette(&catalog);
    let icon = original_icon(icon_resource);
    trace("icon");
    let mut placement = window_state::Placement::load();
    let mut stored_placement = placement;
    let mut save_placement_at = None;
    let mut initial_size = placement.size;
    if let Some(monitor) = event_loop.primary_monitor() {
        let bounds = monitor.size().to_logical::<u32>(monitor.scale_factor());
        initial_size.width = initial_size.width.min(bounds.width).max(window_state::MIN_SIZE.width);
        initial_size.height = initial_size.height.min(bounds.height.saturating_sub(48)).max(window_state::MIN_SIZE.height);
    }
    let window = WindowBuilder::new()
        .with_title(desktop::WINDOW_TITLE)
        .with_window_classname(desktop::WINDOW_CLASS)
        .with_background_color(background)
        .with_decorations(false)
        .with_visible(false)
        .with_min_inner_size(window_state::MIN_SIZE)
        .with_inner_size(initial_size)
        .with_maximized(true)
        .with_window_icon(icon.clone())
        .with_taskbar_icon(icon)
        .build(&event_loop)?;
    trace("window");
    #[cfg(feature = "native-router")]
    let taskbar = taskbar::Taskbar::new().ok();
    #[cfg(feature = "native-router")]
    let mut taskbar_theme = icon_resource;
    #[cfg(feature = "native-router")]
    if let Some(taskbar) = &taskbar { taskbar.apply(HWND(window.hwnd() as _), icon_resource); }
    #[cfg(not(feature = "native-router"))]
    let mut tray_available = false;
    #[cfg(feature = "native-router")]
    let native_tray = tray::native::Tray::start(window.hwnd(), &catalog, usize::from(icon_resource));

    let handler = event_loop.create_proxy();
    let adapter = event_loop.create_proxy();
    let close_proxy = event_loop.create_proxy();
    let backend = Backend::start(window.hwnd(), move |reply| {
        let _ = adapter.send_event(AppEvent::Reply(reply));
    });
    trace("worker");
    let view = WebViewBuilder::new()
        .with_background_color(background)
        .with_initialization_script(format!("window.morphocyteStartup = {startup_palette};"))
        .with_custom_protocol("atlas".into(), |_, request| {
            let found = request.uri().path() == "/";
            let bytes = if found {
                ATLAS_PAGE.as_bytes()
            } else {
                b""
            };
            Response::builder()
                .status(if found { 200 } else { 404 })
                .header("Content-Type", "text/html; charset=utf-8")
                .body(Cow::Borrowed(bytes))
                .expect("static response")
        })
        .with_url("atlas://localhost/")
        .with_navigation_handler(|destination| matches!(destination.as_str(), "http://atlas.localhost/" | "atlas://localhost/"))
        .with_ipc_handler(move |request| {
            if request.body().len() > 65_536 {
                return;
            }
            if let Ok(value) = serde_json::from_str(request.body()) {
                let _ = handler.send_event(AppEvent::Command(value));
            }
        })
        .build(&window)?;
    trace("webview");

    let mut closing: Option<Instant> = None;
    let mut reveal_at = Some(Instant::now() + Duration::from_secs(4));
    let mut starting_update = false;
    let mut updating: Option<update::Prepared> = None;
    event_loop.run(move |event, _, control_flow| {
        let _ = &instance;
        #[cfg(feature = "native-router")]
        let tray_available = native_tray.as_ref().is_some_and(tray::native::Tray::available);
        if save_placement_at.is_some_and(|deadline| Instant::now() >= deadline) {
            save_placement_at = None;
            placement.persist(&mut stored_placement);
        }
        *control_flow = [closing, reveal_at, save_placement_at].into_iter().flatten().min()
            .map_or(ControlFlow::Wait, ControlFlow::WaitUntil);
        if closing.is_some_and(|deadline| Instant::now() >= deadline) {
            if let Some(operation) = updating.take() { operation.abort(); }
            #[cfg(feature = "native-router")]
            taskbar::clear_window(HWND(window.hwnd() as _));
            *control_flow = ControlFlow::Exit;
            return;
        }
        if closing.is_none() && reveal_at.is_some_and(|deadline| Instant::now() >= deadline) {
            reveal_at = None;
            show_initial_window(&window, &view, &mut startup, tray_available);
            *control_flow = save_placement_at.map_or(ControlFlow::Wait, ControlFlow::WaitUntil);
        }
        let start_shutdown = |installing_update: bool| {
            let proxy = close_proxy.clone();
            backend.shutdown(move || {
                let _ = proxy.send_event(AppEvent::Reply(json!({
                    "id": 0, "ok": !installing_update,
                    "error": "Обновление отложено. Модуль VPN завершился до подтверждения остановки."
                })));
            });
            Instant::now() + Duration::from_secs(45)
        };
        let send = |request| {
            if let Some(reply) = backend.send(request) {
                let _ = close_proxy.send_event(AppEvent::Reply(reply));
                return false;
            }
            true
        };
        let close_window = matches!(&event, Event::WindowEvent { event: WindowEvent::CloseRequested, .. })
            || matches!(&event, Event::UserEvent(AppEvent::Command(Value::String(command))) if command == "close");
        let quit = matches!(&event, Event::UserEvent(AppEvent::Command(Value::String(command))) if command == "exit");
        if close_window || quit {
            placement.capture(&window);
            placement.persist(&mut stored_placement);
            if closing.is_none() && !starting_update {
                if close_window && tray_available {
                    window.set_visible(false);
                } else {
                    let deadline = start_shutdown(false);
                    closing = Some(deadline);
                    *control_flow = ControlFlow::WaitUntil(deadline);
                }
            }
            return;
        }
        let ready = reveal_at.is_none();
        let restore = || {
            if ready {
                window.set_visible(true);
                window.set_minimized(false);
                if !window.is_visible() {
                    let _ = unsafe { ShowWindow(HWND(window.hwnd() as _), SW_SHOW) };
                }
                window.set_focus();
                let _ = view.evaluate_script("window.atlasShown?.()");
            }
        };
        match event {
            Event::WindowEvent { event: WindowEvent::Resized(_) | WindowEvent::ScaleFactorChanged { .. }, .. }
                if reveal_at.is_none() && closing.is_none() => {
                placement.capture(&window);
                save_placement_at = (placement != stored_placement).then(|| Instant::now() + Duration::from_millis(300));
                if let Some(deadline) = save_placement_at { *control_flow = ControlFlow::WaitUntil(deadline); }
            }
            Event::UserEvent(AppEvent::Command(Value::String(command))) => match command.as_str() {
                "ui-ready" if closing.is_none() && reveal_at.take().is_some() => {
                    show_initial_window(&window, &view, &mut startup, tray_available);
                    trace("first frame");
                }
                "minimize" => window.set_minimized(true),
                "maximize" => window.set_maximized(!window.is_maximized()),
                "restore" if closing.is_none() => restore(),
                "drag" => {
                    let _ = window.drag_window();
                }
                "elevate" if closing.is_none() && !starting_update => {
                    if let Err(error) = reconnect_as_admin() {
                        deliver(&view, &json!({"ok":false,"error":error}));
                    } else {
                        let deadline = start_shutdown(false);
                        closing = Some(deadline);
                        *control_flow = ControlFlow::WaitUntil(deadline);
                    }
                }
                _ => {}
            },
            Event::UserEvent(AppEvent::Tray(action)) if closing.is_none() && !starting_update => {
                match action {
                    tray::Action::Restore => {
                        startup = false;
                        restore();
                    }
                    tray::Action::Power => {
                        let _ = view.evaluate_script("window.atlasTray?.('power')");
                    }
                    tray::Action::Settings => {
                        restore();
                        let _ = view.evaluate_script("window.atlasTray?.('settings')");
                    }
                    tray::Action::Journal => {
                        restore();
                        let _ = view.evaluate_script("window.atlasTray?.('journal')");
                    }
                    tray::Action::Theme(resource) => {
                        let _ = view.evaluate_script(&format!("window.atlasTray?.('theme', {resource})"));
                    }
                    tray::Action::Exit => {
                        placement.capture(&window);
                        placement.persist(&mut stored_placement);
                        let deadline = start_shutdown(false);
                        closing = Some(deadline);
                        *control_flow = ControlFlow::WaitUntil(deadline);
                    }
                }
            }
            Event::UserEvent(AppEvent::Command(command)) if closing.is_none() => {
                match command["shell"].as_str() {
                    Some("theme-icon") => {
                        if let Some(id @ 1..=9) = command["resource"].as_u64() {
                            if let Some(icon) = original_icon(id as u16) {
                                window.set_window_icon(Some(icon.clone()));
                                window.set_taskbar_icon(Some(icon));
                            }
                            #[cfg(feature = "native-router")]
                            if let Some(tray) = &native_tray { tray.set_theme(id as usize); }
                            #[cfg(feature = "native-router")]
                            if id as u16 != taskbar_theme {
                                taskbar_theme = id as u16;
                                if let Some(taskbar) = &taskbar { taskbar.apply(HWND(window.hwnd() as _), taskbar_theme); }
                            }
                        }
                    }
                    Some(_) => {}
                    None => { send(command); }
                }
            }
            Event::UserEvent(AppEvent::UpdateReady(result)) => {
                match result {
                    Ok(operation) => {
                        placement.capture(&window);
                        placement.persist(&mut stored_placement);
                        updating = Some(operation);
                        let deadline = start_shutdown(true);
                        closing = Some(deadline);
                        *control_flow = ControlFlow::WaitUntil(deadline);
                    }
                    Err(error) => {
                        trace(&format!("updater: {error}"));
                        if !send(json!({"id":-2,"op":"update_handoff_failed"})) { starting_update = false; }
                    }
                }
            }
            Event::UserEvent(AppEvent::Reply(reply)) => {
                if reply["disconnected"] == true {
                    if tray_available && !window.is_visible() && closing.is_none() { restore(); }
                    #[cfg(not(feature = "native-router"))]
                    { tray_available = false; }
                }
                if reply["id"] == 0 && closing.is_some() {
                    if reply["ok"] == true || (reply["disconnected"] == true && updating.is_none()) {
                        if let Some(operation) = updating.take() {
                            if let Err(error) = operation.commit() {
                                trace(&format!("updater commit: {error}"));
                            }
                        }
                        #[cfg(feature = "native-router")]
                        taskbar::clear_window(HWND(window.hwnd() as _));
                        *control_flow = ControlFlow::Exit;
                    } else {
                        closing = None;
                        *control_flow = ControlFlow::Wait;
                        if let Some(operation) = updating.take() {
                            operation.abort();
                            if !send(json!({"id":-2,"op":"update_handoff_failed"})) { starting_update = false; }
                        }
                        deliver(&view, &reply);
                    }
                } else {
                    let value = &reply["value"];
                    let state = value.get("state").unwrap_or(value);
                    if reply["id"] == -2 {
                        starting_update = false;
                        let _ = view.evaluate_script("window.atlasUpdateFailed?.()");
                    }
                    #[cfg(feature = "native-router")]
                    if let Some(tray) = &native_tray
                        && let Some(active) = state["tracked"].as_bool() {
                        tray.set_connected(active);
                    }
                    #[cfg(not(feature = "native-router"))]
                    if let Some(available) = state["trayAvailable"].as_bool() {
                        tray_available = available;
                    }
                    if startup && tray_available && window.is_minimized() {
                        startup = false;
                        window.set_visible(false);
                    }
                    if closing.is_none() && !starting_update
                        && let Some(id) = state["atlasInstallReady"].as_str() {
                        starting_update = true;
                        let id = id.to_owned();
                        let resume = state["online"] == true;
                        let proxy = close_proxy.clone();
                        std::thread::spawn(move || {
                            let _ = proxy.send_event(AppEvent::UpdateReady(update::start(&id, resume)));
                        });
                    }
                    deliver(&view, &reply);
                }
            }
            _ => {}
        }
    });
}

fn shell_execute(verb: &str, target: &std::ffi::OsStr, parameters: &str) -> isize {
    #[link(name = "shell32")]
    unsafe extern "system" {
        fn ShellExecuteW(
            owner: isize, operation: *const u16, file: *const u16, args: *const u16,
            directory: *const u16, show: i32,
        ) -> isize;
    }
    let file: Vec<_> = target.encode_wide().chain(Some(0)).collect();
    let operation: Vec<_> = verb.encode_utf16().chain(Some(0)).collect();
    let args: Vec<_> = parameters.encode_utf16().chain(Some(0)).collect();
    unsafe { ShellExecuteW(0, operation.as_ptr(), file.as_ptr(), args.as_ptr(), std::ptr::null(), 1) }
}

fn reconnect_as_admin() -> Result<(), String> {
    let exe = std::env::current_exe().map_err(|err| err.to_string())?;
    let result = shell_execute("runas", exe.as_os_str(), "--connect-admin");
    if result > 32 {
        Ok(())
    } else if result == 5 {
        Err("Запуск с правами администратора отменён или Windows отказала в доступе.".into())
    } else {
        Err(format!("Windows не запустила Морфоцит с правами администратора (код {result})."))
    }
}

fn deliver(view: &wry::WebView, reply: &Value) {
    let _ = view.evaluate_script(&format!("window.atlasReply?.({reply})"));
}

fn trace(stage: &str) {
    use std::io::Write;
    if let Some(file) = std::env::var_os("ATLAS_STARTUP_TRACE") {
        if let Ok(mut log) = std::fs::OpenOptions::new().append(true).create(true).open(file) {
            let _ = writeln!(log, "{stage}");
        }
    }
}
