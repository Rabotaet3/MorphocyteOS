mod endpoint_probe;
mod profile;
mod processes;
mod router;
mod runtime;
mod speed;
mod startup;
mod storage;
mod update;

use serde_json::{Value, json};
use std::{io, path::PathBuf, sync::mpsc::{self, SyncSender, TrySendError}, time::Duration};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

fn problem(message: impl Into<String>) -> Box<dyn std::error::Error + Send + Sync> {
    io::Error::other(message.into()).into()
}

const SOCKET_RESOURCES: &str = "Windows не хватило ресурсов для открытия сетевого соединения (10055). Повтори попытку позже.";

fn socket_exhausted(error: &(dyn std::error::Error + 'static)) -> bool {
    if let Some(ureq::Error::Io(inner)) = error.downcast_ref::<ureq::Error>() {
        return socket_exhausted(inner);
    }
    if let Some(inner) = error.downcast_ref::<io::Error>() {
        return inner.raw_os_error() == Some(10055) || inner.get_ref().is_some_and(|cause| socket_exhausted(cause));
    }
    if let Some(inner) = error.downcast_ref::<windows::core::Error>() {
        return inner.code() == windows::core::HRESULT::from_win32(10055);
    }
    false
}

fn error_message(error: &(dyn std::error::Error + 'static)) -> String {
    if socket_exhausted(error) { SOCKET_RESOURCES.to_owned() } else { error.to_string() }
}

fn random_token() -> Result<String> {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).map_err(|_| problem("Windows не выдала случайный ключ."))?;
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}

fn http_agent(timeout: Duration) -> ureq::Agent {
    ureq::Agent::new_with_config(ureq::config::Config::builder()
        .proxy(None).max_redirects(0).timeout_global(Some(timeout))
        .tls_config(ureq::tls::TlsConfig::builder().provider(ureq::tls::TlsProvider::NativeTls).build()).build())
}

struct Adapter {
    index: u32,
    kind: u32,
    up: bool,
    hardware: bool,
    name: String,
}

fn adapters() -> Vec<Adapter> {
    use windows::Win32::NetworkManagement::IpHelper::{FreeMibTable, GetIfTable2, MIB_IF_TABLE2};
    let mut table: *mut MIB_IF_TABLE2 = std::ptr::null_mut();
    if unsafe { GetIfTable2(&mut table) }.0 != 0 || table.is_null() { return vec![]; }
    let rows = unsafe { std::slice::from_raw_parts((*table).Table.as_ptr(), (*table).NumEntries as usize) };
    let adapters = rows.iter().map(|row| Adapter {
        index:row.InterfaceIndex, kind:row.Type, up:row.OperStatus.0 == 1,
        hardware:row.InterfaceAndOperStatusFlags._bitfield & 1 != 0,
        name:String::from_utf16_lossy(&row.Alias[..row.Alias.iter().position(|ch| *ch == 0).unwrap_or(row.Alias.len())]),
    }).filter(|row| !row.name.is_empty()).collect();
    unsafe { FreeMibTable(table.cast()); }
    adapters
}

fn physical_uplink() -> Option<String> {
    use windows::Win32::NetworkManagement::IpHelper::GetBestInterface;
    let mut preferred = 0;
    unsafe { GetBestInterface(u32::from_ne_bytes([1, 1, 1, 1]), &mut preferred); }
    let candidates = adapters().into_iter().filter(|row| row.up && row.hardware && matches!(row.kind, 6 | 71)).collect::<Vec<_>>();
    candidates.iter().find(|row| row.index == preferred)
        .or_else(|| (candidates.len() == 1).then(|| &candidates[0])).map(|row| row.name.clone())
}

fn loopback() -> Option<String> { adapters().into_iter().find(|row| row.kind == 24).map(|row| row.name) }

fn tunnel_in_use() -> bool {
    adapters().iter().any(|row| row.up && row.name.eq_ignore_ascii_case("MorphocyteTUN"))
}

pub fn root() -> io::Result<PathBuf> {
    Ok(std::env::current_exe()?.with_file_name("backend"))
}

pub fn theme() -> Option<String> {
    let root = root().ok()?;
    let settings = storage::load(&root);
    settings["Theme"].as_str().map(str::to_owned)
}

pub struct Backend {
    sender: SyncSender<Value>,
}

impl Backend {
    pub fn start(window: isize, reply: impl Fn(Value) + Send + 'static) -> Self {
        let (sender, receiver) = mpsc::sync_channel::<Value>(8);
        std::thread::spawn(move || {
            let mut state = root().map(|path| router::Router::load(path, window));
            let elevated_start = std::env::args().any(|arg| arg == "--connect-admin");
            let windows_logon = std::env::args().any(|arg| arg == "--startup");
            if let Ok(router) = &mut state
                && (elevated_start || router.auto_connect(windows_logon))
            {
                router.connect_on_startup();
            }
            for request in receiver {
                let shutdown = request["op"] == "shutdown";
                let result = match &mut state {
                    Ok(router) => router.execute(&request),
                    Err(_) => Err(problem("Не удалось определить папку данных Морфоцита.")),
                };
                match result {
                    Ok(value) => reply(json!({"id":request["id"],"ok":true,"value":value})),
                    Err(error) => reply(failed(&request, &error_message(error.as_ref()))),
                }
                if shutdown { break; }
            }
        });
        Self { sender }
    }

    pub fn send(&self, request: Value) -> Option<Value> {
        match self.sender.try_send(request) {
            Ok(()) => None,
            Err(TrySendError::Full(request)) => Some(failed(&request, "Дождись завершения предыдущей операции.")),
            Err(TrySendError::Disconnected(request)) => Some(json!({"id":request["id"],"ok":false,
                "disconnected":true,"error":"Служба Морфоцита завершила работу."})),
        }
    }

    pub fn shutdown(&self, already_stopped: impl FnOnce() + Send + 'static) {
        let sender = self.sender.clone();
        std::thread::spawn(move || {
            if sender.send(json!({"id":0,"op":"shutdown"})).is_err() { already_stopped(); }
        });
    }
}

fn failed(request: &Value, message: &str) -> Value {
    json!({"id":request["id"],"ok":false,"error":message})
}

pub fn check_profile(root: &std::path::Path, path: &std::path::Path, live: bool, validate_tun: bool) -> Result<usize> {
    let profile = profile::Profile::load(path)?;
    let count = profile.servers().len();
    if live {
        let mut session = runtime::Session::start(root, &profile, "", false, false)?;
        if !session.running() { return Err(problem("Проверочное ядро завершилось.")); }
    }
    if validate_tun { runtime::Session::validate_tunnel(root, &profile)?; }
    Ok(count)
}

