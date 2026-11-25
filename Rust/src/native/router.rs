use super::{Result, error_message, problem, random_token, socket_exhausted, storage};
use super::endpoint_probe::{Endpoint, Method};
use super::profile::{Profile, list, text};
use super::runtime::{Controller, ProbeUnavailable, Session};
use super::{processes, speed, startup, update::Update};
use serde_json::{Value, json};
use std::{
    collections::{HashMap, VecDeque},
    path::{Path, PathBuf},
    sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const THEME: &str = "Тёмная морфоцитная";
const THEMES: [&str; 9] = [THEME,"Светлая морфоцитная","Аврора","Космический градиент",
    "Графит","Океан","Аметист","Закат","Тёплый свет"];
const SOCKET_RETRY: Duration = Duration::from_secs(30);
const PING_RETRY_DELAY: Duration = Duration::from_secs(2);
const PING_INTERVAL: Duration = Duration::from_millis(125);

#[derive(Default)]
struct Measurements {
    method: Method,
    latest: HashMap<String, PingResult>,
    last_success: HashMap<String, (i64, Method)>,
    issue: String,
    retry_after: Option<Instant>,
}

#[derive(Clone, Copy)]
enum PingResult {
    Measured(i64),
    NoResponse,
    Error,
    Skipped(&'static str),
}

struct PingBatch {
    cancelled: Arc<AtomicBool>,
    task: JoinHandle<()>,
}

#[derive(Default)]
struct OnlineStats {
    receive: u64,
    send: u64,
    health: Option<Option<i64>>,
}

impl PingBatch {
    fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
        self.task.thread().unpark();
    }

    fn wait(self) {
        self.cancel();
        let _ = self.task.join();
    }
}

fn wait_for_ping(stop: &AtomicBool, delay: Duration) -> bool {
    let deadline = Instant::now() + delay;
    while !stop.load(Ordering::Acquire) {
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() { return true; }
        thread::park_timeout(left);
    }
    false
}

fn retry_ping<T>(stop: &AtomicBool, delay: Duration, mut check: impl FnMut() -> Result<T>) -> Result<T> {
    let result = check();
    if result.as_ref().is_err_and(|error| socket_exhausted(error.as_ref())) && wait_for_ping(stop, delay) {
        return check();
    }
    result
}

fn ping_vpn_batch<'a>(mut names: impl Iterator<Item = &'a str>, stop: &AtomicBool,
    measured: &Mutex<Measurements>, check: impl Fn(&str) -> Result<PingResult> + Sync) {
    let mut parallel = true;
    while let Some(name) = names.next() {
        if stop.load(Ordering::Acquire) { break; }
        let other = if parallel { names.next() } else { None };
        let (first, second) = if let Some(other) = other {
            thread::scope(|scope| {
                let second = scope.spawn(|| check(other));
                let first = check(name);
                (first, Some((other, second.join().expect("second ping worker"))))
            })
        } else {
            (check(name), None)
        };
        if stop.load(Ordering::Acquire) { break; }

        let mut retry = Vec::new();
        for (name, result) in [Some((name, first)), second].into_iter().flatten() {
            if result.as_ref().is_err_and(|error| socket_exhausted(error.as_ref())) {
                retry.push(name);
            } else {
                measured.lock().expect("ping state").complete(name, Method::Vpn, result);
            }
        }
        for name in retry {
            parallel = false;
            if !wait_for_ping(stop, PING_RETRY_DELAY) { return; }
            let result = check(name);
            if stop.load(Ordering::Acquire) { return; }
            if !measured.lock().expect("ping state").complete(name, Method::Vpn, result) { return; }
        }
        if !wait_for_ping(stop, PING_INTERVAL) { break; }
    }
}

impl Measurements {
    fn record(&mut self, name: &str, method: Method, result: PingResult) {
        if let PingResult::Measured(delay) = result {
            self.last_success.insert(name.to_owned(), (delay, method));
        }
        if method == self.method {
            self.latest.insert(name.to_owned(), result);
        }
    }

    fn record_issue(&mut self, error: &(dyn std::error::Error + 'static)) {
        self.issue = error_message(error);
        if socket_exhausted(error) { self.retry_after = Some(Instant::now() + SOCKET_RETRY); }
    }

    fn complete(&mut self, name: &str, method: Method, result: Result<PingResult>) -> bool {
        let measurement = match result {
            Ok(result) => result,
            Err(error) if error.is::<ProbeUnavailable>() => PingResult::NoResponse,
            Err(error) if socket_exhausted(error.as_ref()) => {
                self.record_issue(error.as_ref());
                return false;
            }
            Err(_) => {
                self.issue = "Не удалось завершить часть локальных замеров.".to_owned();
                PingResult::Error
            }
        };
        self.record(name, method, measurement);
        true
    }
}

pub(crate) struct Router {
    root: PathBuf,
    window: isize,
    settings: Value,
    launch_at_sign_in: bool,
    path: PathBuf,
    profile: Option<Profile>,
    servers: Vec<Value>,
    draft: Option<Profile>,
    session: Option<Session>,
    selected: String,
    ping: Option<PingBatch>,
    speed: Option<speed::Test>,
    measured: Arc<Mutex<Measurements>>,
    issue: String,
    stats: Arc<Mutex<OnlineStats>>,
    monitor: Option<(Arc<AtomicBool>, thread::Thread)>,
    logs: VecDeque<String>,
    update: Update,
}

impl Router {
    pub(crate) fn load(root: PathBuf, window: isize) -> Self {
        let settings = storage::load(&root);
        let path = storage::resolve(&root, settings["ConfigPath"].as_str().unwrap_or("Profiles/default.yaml"));
        let profile = Profile::load(&path).ok();
        let servers: Vec<_> = profile.iter().flat_map(Profile::servers).map(describe).collect();
        let mut update = Update::load(&root);
        if settings["AutoCheckUpdates"] == true && update.message().is_empty() { update.check(); }
        let selected = settings["NativeSelected"].as_str()
            .or_else(|| settings["ProfileSelections"].get(path.to_string_lossy().as_ref()).and_then(Value::as_str))
            .filter(|name| servers.iter().any(|node| text(node, "name") == *name))
            .or_else(|| servers.first().map(|node| text(node, "name")))
            .unwrap_or_default().to_owned();
        Self {
            root, window, settings, launch_at_sign_in: startup::enabled().unwrap_or(false),
            path, profile, servers, draft: None, session: None, selected, ping: None, speed: None,
            measured: Arc::new(Mutex::new(Measurements::default())), issue: String::new(),
            stats: Arc::new(Mutex::new(OnlineStats::default())), monitor:None,
            logs: VecDeque::from(["Запущен самостоятельный модуль Rust.".to_owned()]),
            update,
        }
    }

    pub(crate) fn auto_connect(&self, windows_logon: bool) -> bool {
        self.settings[if windows_logon { "AutoConnectOnStartup" } else { "AutoConnectOnOpen" }] == true
    }

    pub(crate) fn connect_on_startup(&mut self) {
        if let Err(error) = self.connect() { self.issue = error_message(error.as_ref()); }
    }

    fn log(&mut self, message: &str) {
        if self.logs.len() >= 80 { self.logs.pop_front(); }
        self.logs.push_back(message.to_owned());
    }

    fn save(&self) -> Result<()> {
        storage::save_file(&self.root.join("native-settings.json"), &serde_json::to_vec_pretty(&self.settings)?)
    }

    fn save_preferences(&mut self, request: &Value) -> Result<()> {
        let launch = request["launchAtSignIn"] == true;
        let mut updated = self.settings.clone();
        updated["InterfaceScale"] = json!(request["scale"].as_f64().unwrap_or(1.0).clamp(0.8, 1.5));
        updated["AutoConnectOnStartup"] = json!(launch && request["connectAtSignIn"] == true);
        updated["AutoConnectOnOpen"] = json!(request["connectOnOpen"] == true);
        updated["AutoCheckUpdates"] = json!(request["automaticUpdates"] == true);
        storage::save_file(&self.root.join("native-settings.json"), &serde_json::to_vec_pretty(&updated)?)?;
        if launch != self.launch_at_sign_in && let Err(error) = startup::set_enabled(launch) {
            self.save()?;
            return Err(problem(format!("Планировщик Windows не принял изменение автозапуска. {error}")));
        }
        self.launch_at_sign_in = launch;
        self.settings = updated;
        Ok(())
    }

    fn can_tun() -> bool {
        unsafe { windows::Win32::UI::Shell::IsUserAnAdmin().as_bool() }
    }

    fn connect(&mut self) -> Result<()> {
        if !Self::can_tun() { return Err(problem("Для запуска TUN нужны права администратора Windows.")); }
        if self.selected.is_empty() { return Err(problem("Добавь подписку или выбери профиль VPN.")); }
        if super::tunnel_in_use() { return Err(problem("MorphocyteTUN уже используется. Останови VPN в другой копии Морфоцита.")); }
        let profile = self.profile.as_ref().ok_or_else(|| problem("Выбери профиль VPN."))?;
        if let Some(batch) = self.ping.take() { batch.wait(); }
        let full_tunnel = self.settings["FullTunnel"] == true;
        let start = || Session::start(&self.root, profile, &self.selected, true, full_tunnel);
        let session = match start() {
            Err(error) if socket_exhausted(error.as_ref()) => {
                thread::sleep(Duration::from_secs(1));
                start()?
            }
            result => result?,
        };
        self.start_monitor(session.controller());
        self.session = Some(session);
        self.issue.clear();
        self.log("Ядро sing-box запущено напрямую из Морфоцита.");
        Ok(())
    }

    fn disconnect(&mut self) {
        if let Some(test) = self.speed.take() { test.cancel(); }
        self.stop_monitor();
        let batch = self.ping.take();
        if let Some(batch) = &batch { batch.cancel(); }
        if self.session.take().is_some() { self.log("VPN остановлен."); }
        if let Some(batch) = batch { batch.wait(); }
    }

    fn stop_monitor(&mut self) {
        if let Some((cancel, worker)) = self.monitor.take() {
            cancel.store(true, Ordering::Release);
            worker.unpark();
        }
        self.stats = Arc::new(Mutex::new(OnlineStats::default()));
    }

    fn start_monitor(&mut self, controller: Controller) {
        self.stop_monitor();
        let stop = Arc::new(AtomicBool::new(false));
        let data = Arc::clone(&self.stats);
        let traffic_stop = Arc::clone(&stop);
        let worker = thread::spawn(move || {
            let mut retry = Duration::from_secs(1);
            while !traffic_stop.load(Ordering::Acquire) {
                let result = controller.stream_traffic(&traffic_stop, |receive, send| {
                    retry = Duration::from_secs(1);
                    let mut stats = data.lock().expect("online statistics");
                    stats.receive = receive;
                    stats.send = send;
                });
                if traffic_stop.load(Ordering::Acquire) { break; }
                {
                    let mut stats = data.lock().expect("online statistics");
                    stats.receive = 0;
                    stats.send = 0;
                }
                let delay = if result.as_ref().is_err_and(|error| socket_exhausted(error.as_ref())) {
                    SOCKET_RETRY
                } else {
                    retry
                };
                thread::park_timeout(delay);
                retry = (delay * 2).min(SOCKET_RETRY);
            }
        });
        self.monitor = Some((stop, worker.thread().clone()));
    }

    fn ping_start(&mut self, method: Method) -> Result<()> {
        if self.speed.as_ref().is_some_and(speed::Test::running) { return Ok(()); }
        if self.ping.as_ref().is_some_and(|batch| !batch.task.is_finished()) { return Ok(()); }
        if let Some(batch) = self.ping.take() { batch.wait(); }
        if self.measured.lock().expect("ping state").retry_after.is_some_and(|until| Instant::now() < until) { return Ok(()); }
        let Some(profile) = self.profile.clone() else { return Ok(()); };
        let targets = profile.servers().into_iter().map(|node| {
            (text(node,"name").to_owned(), (method != Method::Vpn).then(|| Endpoint::from_node(node)).flatten())
        }).collect::<Vec<_>>();
        if targets.is_empty() { return Ok(()); }
        let loopback_fixture = cfg!(feature = "isolated-check")
            && targets.iter().filter_map(|(_, endpoint)| endpoint.as_ref()).all(Endpoint::is_loopback);
        if method != Method::Vpn && (self.session.is_some() || (!loopback_fixture && super::tunnel_in_use())) {
            return Err(problem("Останови VPN Морфоцита перед проверкой адресов напрямую."));
        }
        let active = self.session.as_ref().map(Session::prober);
        let root = self.root.clone();
        let cancelled = Arc::new(AtomicBool::new(false));
        let stop = Arc::clone(&cancelled);
        let measured = Arc::clone(&self.measured);
        {
            let mut state = measured.lock().expect("ping state");
            if state.method != method { state.latest.clear(); }
            state.method = method;
            state.issue.clear();
            state.retry_after = None;
        }
        let task = thread::spawn(move || {
            if stop.load(Ordering::Acquire) { return; }
            let mut testing = None;
            let probe = match (method, active) {
                (Method::Vpn, Some(probe)) => Some(probe),
                (Method::Vpn, None) => match retry_ping(&stop, PING_RETRY_DELAY, ||
                    Session::start(&root, &profile, "", false, false)) {
                    Ok(session) => {
                        let probe = session.prober();
                        testing = Some(session);
                        Some(probe)
                    }
                    Err(error) => {
                        if !stop.load(Ordering::Acquire) {
                            measured.lock().expect("ping state").record_issue(error.as_ref());
                        }
                        return;
                    }
                },
                _ => None,
            };
            let probe = match probe.transpose() {
                Ok(probe) => probe,
                Err(error) => {
                    measured.lock().expect("ping state").record_issue(error.as_ref());
                    return;
                }
            };
            if let Some(probe) = probe {
                ping_vpn_batch(targets.iter().map(|(name, _)| name.as_str()), &stop, &measured, |name|
                    probe.latency(name, Some(&stop)).map(PingResult::Measured));
                drop(testing);
                return;
            }
            let mut direct_results = HashMap::new();
            for (name, endpoint) in &targets {
                if stop.load(Ordering::Acquire) { break; }
                let result = if let Some(endpoint) = endpoint {
                    if let Some(reason) = endpoint.unsupported(method) {
                        Ok(PingResult::Skipped(reason))
                    } else {
                        let key = endpoint.probe_key(method);
                        match direct_results.get(&key) {
                            Some(&result) => Ok(result),
                            None => retry_ping(&stop, PING_RETRY_DELAY, || endpoint.measure(method, &stop)).map(|delay| {
                                let result = delay.map_or(PingResult::NoResponse, PingResult::Measured);
                                direct_results.insert(key, result);
                                result
                            }),
                        }
                    }
                } else {
                    Ok(PingResult::Skipped("У профиля нет одного адреса для прямой проверки."))
                };
                if stop.load(Ordering::Acquire) { break; }
                if !measured.lock().expect("ping state").complete(name, method, result) { break; }
                if !wait_for_ping(&stop, PING_INTERVAL) { break; }
            }
            drop(testing);
        });
        self.ping = Some(PingBatch { cancelled, task });
        Ok(())
    }

    fn set_profile(&mut self, profile: Profile, source: Option<(String, Value)>) -> Result<()> {
        let nodes = profile.servers();
        let Some(first) = nodes.first() else { return Err(problem("В профиле отсутствуют VPN-серверы.")); };
        if self.session.is_some() { return Err(problem("Останови VPN перед заменой профиля.")); }
        if let Some(batch) = self.ping.take() { batch.wait(); }
        Session::validate_tunnel(&self.root, &profile)?;
        let path = self.root.join("Profiles").join(format!("native-{}.yaml", &random_token()?[..12]));
        let chosen = nodes.iter().find(|node| text(node, "name") == self.selected)
            .map(|node| text(node, "name")).unwrap_or_else(|| text(first, "name")).to_owned();
        let updated = source.map(|(secret, metadata)| json!({"ProtectedUrl":secret,"Metadata":metadata}));
        let mut settings = self.settings.clone();
        settings["NativeSelected"] = json!(chosen);
        settings["ConfigPath"] = json!(path.strip_prefix(&self.root)?.to_string_lossy());
        settings["NativeSubscription"] = updated.unwrap_or(Value::Null);
        storage::save_file(&path, serde_yaml_ng::to_string(&profile.data)?.as_bytes())?;
        storage::save_file(&self.root.join("native-settings.json"), &serde_json::to_vec_pretty(&settings)?)?;
        self.settings = settings;
        self.path = path;
        self.selected = chosen;
        self.servers = nodes.into_iter().map(describe).collect();
        self.profile = Some(profile);
        self.draft = None;
        self.measured = Arc::new(Mutex::new(Measurements::default()));
        self.log("Профиль VPN загружен.");
        Ok(())
    }

    fn source(&self) -> Option<&Value> {
        if let Some(value) = self.settings.get("NativeSubscription") { return (!value.is_null()).then_some(value); }
        self.settings["ProfileSubscriptions"].as_object()?.iter()
            .find(|(path, _)| normalized(&storage::resolve(&self.root, path)) == normalized(&self.path))
            .map(|(_, value)| value)
    }

    fn subscription_url(&self) -> Result<Option<String>> {
        self.source().and_then(|source| source["ProtectedUrl"].as_str())
            .map(|protected| storage::read_url(protected)
                .map_err(|_| problem("Сохранённая ссылка недоступна. Укажи её заново.")))
            .transpose()
    }

    fn download_profile(&mut self, source: &str) -> Result<()> {
        if self.session.is_some() { return Err(problem("Останови VPN перед обновлением подписки.")); }
        if let Some(batch) = self.ping.take() { batch.wait(); }
        let (mut profile, metadata) = storage::download(&self.root, source)?;
        if let Some(previous) = &self.profile {
            let mut rules = list(&profile.data, "rules").to_vec();
            for rule in list(&previous.data, "rules").iter().rev().filter(|rule| editable(rule).is_some()) {
                if !rules.contains(rule) { rules.insert(0,rule.clone()); }
            }
            profile.set_rules(&rules);
        }
        self.set_profile(profile, Some((storage::protect_url(source)?,metadata)))
    }

    fn route_rules(&self) -> Vec<Value> {
        self.draft.as_ref().or(self.profile.as_ref()).map_or_else(Vec::new, |profile|
            list(&profile.data, "rules").iter().filter_map(editable).collect())
    }

    fn edit(&mut self, op: &str, request: &Value) -> Result<()> {
        let mut next = self.draft.as_ref().or(self.profile.as_ref()).cloned()
            .ok_or_else(|| problem("Выбери YAML-профиль."))?;
        let mut raw = list(&next.data, "rules").to_vec();
        let kind = text(request,"kind");
        let name = text(request,"value").trim();
        if name.is_empty() || name.contains(',') || name.chars().any(char::is_control)
            || !matches!(kind,"DOMAIN"|"DOMAIN-SUFFIX"|"DOMAIN-KEYWORD"|"PROCESS-NAME"|"PROCESS-PATH"|"IP-CIDR"|"IP-CIDR6")
        {
            return Err(problem("Введите корректное правило маршрутизации."));
        }
        let target = text(request,"route");
        if matches!(op,"rule_add"|"rule_route") && !matches!(target,"VPN"|"DIRECT"|"REJECT") {
            return Err(problem("Неизвестный маршрут правила."));
        }
        let positions = raw.iter().enumerate().filter_map(|(index, value)| editable(value).map(|rule| (index,rule))).collect::<Vec<_>>();
        if op == "rule_add" {
            if positions.iter().any(|(_, value)| value["kind"] == kind && value["value"] == name) {
                return Err(problem("Такое правило уже добавлено."));
            }
            let point = insert_before_default(&raw);
            raw.insert(point,json!(format!("{kind},{name},{target}")));
        } else {
            let index = request["index"].as_u64().unwrap_or(u64::MAX) as usize;
            let Some((point, previous)) = positions.get(index).filter(|(_, rule)| rule["kind"] == kind && rule["value"] == name)
                else { return Err(problem("Список правил изменился. Повтори действие.")); };
            match op {
                "rule_remove" => { raw.remove(*point); }
                "rule_route" => raw[*point] = json!(format!("{kind},{name},{target}")),
                "rule_toggle" => {
                    let enabled = previous["enabled"] == true;
                    let line = raw[*point].as_str().unwrap_or_default();
                    raw[*point] = if enabled { json!(format!("# disabled: {line}")) }
                        else { json!(line.trim_start_matches("# disabled: ")) };
                }
                _ => unreachable!(),
            }
        }
        next.set_rules(&raw);
        self.draft = Some(next);
        Ok(())
    }

    fn add_process_rules(&mut self, request: &Value) -> Result<()> {
        let target = text(request, "route");
        if !matches!(target, "VPN" | "DIRECT" | "REJECT") { return Err(problem("Неизвестный маршрут правила.")); }
        let names = list(request, "names");
        if names.is_empty() || names.len() > 512 || !names.iter().all(|name| name.as_str().is_some_and(processes::valid_name)) {
            return Err(problem("Выбери корректные имена запущенных приложений."));
        }
        let mut profile = self.draft.as_ref().or(self.profile.as_ref()).cloned()
            .ok_or_else(|| problem("Сначала загрузи подписку или выбери VPN-профиль."))?;
        let mut rules = list(&profile.data, "rules").to_vec();
        let mut used = rules.iter().filter_map(editable).filter(|rule| rule["kind"] == "PROCESS-NAME")
            .map(|rule| text(&rule, "value").to_lowercase()).collect::<std::collections::HashSet<_>>();
        let additions = names.iter().filter_map(Value::as_str).filter(|name| used.insert(name.to_lowercase()))
            .map(|name| json!(format!("PROCESS-NAME,{name},{target}"))).collect::<Vec<_>>();
        if additions.is_empty() { return Ok(()); }
        let point = insert_before_default(&rules);
        rules.splice(point..point, additions);
        profile.set_rules(&rules);
        self.draft = Some(profile);
        Ok(())
    }

    fn state(&mut self) -> Value {
        self.update.collect();
        let online = self.session.as_mut().is_some_and(Session::running);
        if !online && self.session.take().is_some() {
            if let Some(test) = self.speed.take() { test.cancel(); }
            self.stop_monitor();
            self.issue = "Ядро VPN завершилось.".to_owned();
        }
        let (server_pings, ping_errors, ping_skipped, ping_issue, ping_method, ping_retry_seconds, last_ping) = {
            let ping = self.measured.lock().expect("ping state");
            let mut delays = serde_json::Map::new();
            let mut errors = Vec::new();
            let mut skipped = serde_json::Map::new();
            for (name, measurement) in &ping.latest {
                match measurement {
                    PingResult::Measured(delay) => { delays.insert(name.clone(), json!(delay)); }
                    PingResult::NoResponse => { delays.insert(name.clone(), Value::Null); }
                    PingResult::Error => errors.push(name.clone()),
                    PingResult::Skipped(reason) => { skipped.insert(name.clone(), json!(reason)); }
                }
            }
            errors.sort_unstable();
            let retry_seconds = ping.retry_after.map_or(0, |until| until.saturating_duration_since(Instant::now())
                .as_millis().div_ceil(1000) as u32);
            (delays, errors, skipped, ping.issue.clone(), ping.method.key(), retry_seconds,
                ping.last_success.get(&self.selected).copied())
        };
        let preferences = json!({"theme":self.settings["Theme"],"interfaceScale":self.settings["InterfaceScale"].as_f64().unwrap_or(1.0),
            "autoCheckUpdates":self.settings["AutoCheckUpdates"] == true,"launchAtSignIn":self.launch_at_sign_in,"autoConnectOnOpen":self.auto_connect(false),
            "autoConnectOnStartup":self.launch_at_sign_in && self.auto_connect(true),"autoReconnect":false,"profilePath":self.path.to_string_lossy(),
            "corePath":self.root.join("ThirdPartySource/Engine/sing-box.exe").to_string_lossy(),
            "updateMessage":self.update.message(),"atlasVersion":env!("CARGO_PKG_VERSION")});
        let subscription = self.source().and_then(|value| value.get("Metadata")).map(storage::subscription_view);
        let stats = self.stats.lock().expect("online statistics");
        let speed = self.speed.as_ref().map(speed::Test::display).unwrap_or_default();
        let mut state = json!({
            "nativePreview":true,"theme":self.settings["Theme"].as_str().unwrap_or(THEME),"themes":THEMES,
            "preferences":preferences,"trayAvailable":false,
            "online":online,"tracked":online,"ready":!self.selected.is_empty(),"requiresElevation":!Self::can_tun(),
            "mode":if self.settings["FullTunnel"] == true {"FullTunnel"} else {"Rules"},
            "core":"sing-box","selected":self.selected,"status":if online {"VPN запущен"} else {"VPN выключен"},
            "issue":self.issue,"receive":rate(stats.receive),"send":rate(stats.send),
            "latency":last_ping.map(|(delay, _)| format!("{delay} мс")).unwrap_or_else(|| "—".to_owned()),
            "latencyMethod":last_ping.map(|(_, method)| method.key()),
            "maxDownload":speed.speed,"speedAvailable":self.session.as_ref().and_then(Session::speed_channel).is_some(),
            "speedRunning":speed.running,"speedProgress":speed.percent.map(|amount| format!("{amount} %")),
            "speedMessage":speed.message,"healthChecked":stats.health.is_some(),
            "healthOk":stats.health.flatten().is_some(),"servers":self.servers,"rules":self.route_rules(),
            "serverPings":server_pings,"serverPingErrors":ping_errors,
            "serverPingSkipped":ping_skipped,"serverPingMethod":ping_method,
            "pingRunning":self.ping.as_ref().is_some_and(|batch| !batch.task.is_finished()),"pingIssue":ping_issue,
            "pingRetrySeconds":ping_retry_seconds,
            "dirty":self.draft.is_some(),"logs":self.logs,"subscription":subscription
        });
        state["atlasUpdate"] = json!(self.update.version());
        state["atlasChecking"] = json!(self.update.checking());
        state["atlasInstallRunning"] = json!(self.update.installing());
        state["atlasInstallReady"] = json!(self.update.ready());
        state["atlasInstall"] = json!(self.update.progress());
        state
    }

    pub(crate) fn execute(&mut self, request: &Value) -> Result<Value> {
        let op = text(request,"op");
        match op {
            "state" => {}
            "check_updates" => self.update.check(),
            "update_install" => self.update.install(&self.root, text(request, "version"))?,
            "update_handoff_failed" => self.update.failed(),
            "shutdown" => { self.disconnect(); }
            "power" => { if self.session.is_some() { self.disconnect(); } else { self.connect()?; } }
            "restart" => { self.disconnect(); self.connect()?; }
            "speed_start" => {
                if !self.speed.as_ref().is_some_and(speed::Test::running) {
                    let channel = self.session.as_ref().and_then(Session::speed_channel)
                        .ok_or_else(|| problem("Подключи VPN перед замером скорости."))?;
                    if let Some(batch) = self.ping.take() { batch.wait(); }
                    self.speed = Some(speed::Test::start(channel)?);
                }
            }
            "speed_cancel" => {
                if let Some(test) = &self.speed { test.cancel(); }
            }
            "ping_start" => self.ping_start(match request["method"].as_str().unwrap_or("vpn") {
                "vpn" => Method::Vpn,
                "tcp" => Method::Tcp,
                "icmp" => Method::Icmp,
                _ => return Err(problem("Неизвестный способ проверки сервера.")),
            })?,
            "ping_cancel" => {
                if let Some(batch) = &self.ping { batch.cancel(); }
            }
            "check" => {
                if self.speed.as_ref().is_some_and(speed::Test::running) {
                    return Err(problem("Дождись окончания замера скорости."));
                }
                let session = self.session.as_ref().ok_or_else(|| problem("Запусти VPN перед проверкой."))?;
                if let Some(batch) = self.ping.take() { batch.wait(); }
                let delay = session.latency(&self.selected);
                if let Ok(value) = &delay {
                    self.measured.lock().expect("ping state").record(&self.selected, Method::Vpn, PingResult::Measured(*value));
                }
                self.stats.lock().expect("online statistics").health = match &delay {
                    Ok(delay) => Some(Some(*delay)),
                    Err(error) if error.is::<ProbeUnavailable>() => Some(None),
                    Err(_) => None,
                };
                self.issue = delay.as_ref().err().map(|error| error_message(error.as_ref())).unwrap_or_default();
                return Ok(json!({"result":delay.ok().map(|value| format!("{value} мс")).unwrap_or_else(|| "Нет ответа".into()),
                    "state":self.state()}));
            }
            "select" => {
                let chosen = text(request, "name");
                if !self.servers.iter().any(|node| text(node, "name") == chosen) {
                    return Err(problem("Профиль сервера не найден."));
                }
                if self.selected != chosen {
                    let old = std::mem::replace(&mut self.selected, chosen.to_owned());
                    if self.session.is_some() {
                        self.disconnect();
                        if let Err(error) = self.connect() {
                            self.selected = old;
                            let _ = self.connect();
                            return Err(error);
                        }
                    }
                    self.settings["NativeSelected"] = json!(self.selected);
                    self.save()?;
                }
            }
            "mode" => {
                let full = match text(request,"mode") {
                    "FullTunnel" => true, "Rules" => false, _ => return Err(problem("Неизвестный режим.")),
                };
                let old = self.settings["FullTunnel"].clone();
                self.settings["FullTunnel"] = json!(full);
                if self.session.is_some() {
                    self.disconnect();
                    if let Err(error) = self.connect() {
                        self.settings["FullTunnel"] = old;
                        let _ = self.connect();
                        return Err(error);
                    }
                }
                self.save()?;
            }
            "theme" => {
                let theme = text(request,"name");
                if !THEMES.contains(&theme) { return Err(problem("Неизвестная тема.")); }
                self.settings["Theme"] = json!(theme);
                self.save()?;
            }
            "preferences" => self.save_preferences(request)?,
            "pick_yaml" | "import_profile" => {
                if let Some(path) = storage::pick_file(self.window,"Выбрать VPN-профиль",false)? {
                    self.set_profile(Profile::load(&path)?,None)?;
                }
            }
            "subscription_url" => return Ok(json!({"url":self.subscription_url()?})),
            "subscription_save" => {
                let result = self.download_profile(text(request,"url"));
                return Ok(json!({"applied":result.is_ok(),
                    "message":result.err().map(|error| error_message(error.as_ref())).unwrap_or_else(|| "Подписка сохранена.".into()),
                    "state":self.state()}));
            }
            "refresh" => {
                let url = self.subscription_url()?
                    .ok_or_else(|| problem("Сначала добавь ссылку подписки."))?;
                self.download_profile(&url)?;
            }
            "rule_add" | "rule_toggle" | "rule_remove" | "rule_route" => self.edit(op,request)?,
            "running_apps" => return processes::running_apps(),
            "rule_add_processes" => self.add_process_rules(request)?,
            "apply" => {
                if self.session.is_some() { return Err(problem("Останови VPN перед применением правил.")); }
                if let Some(profile) = self.draft.clone() {
                    let source = self.source().and_then(|value| value["ProtectedUrl"].as_str()
                        .map(|url| (url.to_owned(),value["Metadata"].clone())));
                    self.set_profile(profile,source)?;
                }
            }
            "connections" => {
                let Some(session) = &self.session else { return Ok(json!([])); };
                let data = session.api("connections")?;
                return Ok(json!(list(&data,"connections").iter().take(500).map(|entry| {
                    let metadata = &entry["metadata"];
                    let host = text(metadata,"host");
                    let host = if host.is_empty() { text(metadata,"destinationIP") } else { host };
                    let path = text(metadata,"processPath");
                    json!({"process":path.rsplit(['\\','/']).next().unwrap_or_default(),
                        "destination":format!("{host}:{}",metadata["destinationPort"].as_str().unwrap_or_default()),
                        "network":text(metadata,"network").to_uppercase(),"rule":entry["rule"],
                        "route":list(entry,"chains").iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" ← ")})
                }).collect::<Vec<_>>()));
            }
            "create_template" => {
                let template = json!({"proxies":[],"proxy-groups":[],"rules":["MATCH,DIRECT"]});
                if let Some(path) = storage::pick_file(self.window,"Сохранить шаблон YAML",true)? {
                    storage::save_file(&path,serde_yaml_ng::to_string(&template)?.as_bytes())?;
                }
            }
            _ => return Err(problem("Эта возможность ещё переносится в самостоятельную сборку.")),
        }
        Ok(self.state())
    }
}

fn normalized(path: &Path) -> String { path.to_string_lossy().replace('/', "\\").to_lowercase() }

fn rate(bytes: u64) -> String {
    if bytes < 1024 { return format!("{bytes} Б/с"); }
    let (scale, unit) = match bytes {
        ..1_048_576 => (1024.0, "КБ/с"),
        ..1_073_741_824 => (1_048_576.0, "МБ/с"),
        _ => (1_073_741_824.0, "ГБ/с"),
    };
    format!("{:.1} {unit}",bytes as f64 / scale).replace('.',",")
}

fn insert_before_default(rules: &[Value]) -> usize {
    rules.iter().position(|rule| rule.as_str().and_then(|line| line.split_once(','))
        .is_some_and(|(kind, _)| matches!(kind.trim(), "MATCH" | "FINAL"))).unwrap_or(rules.len())
}

fn editable(value: &Value) -> Option<Value> {
    let line = value.as_str()?;
    let disabled = line.starts_with("# disabled: ");
    let line = line.trim_start_matches("# disabled: ");
    let [kind,name,route,..] = line.split(',').map(str::trim).collect::<Vec<_>>()[..] else { return None; };
    if !matches!(kind,"DOMAIN"|"DOMAIN-SUFFIX"|"DOMAIN-KEYWORD"|"PROCESS-NAME"|"PROCESS-PATH"|"IP-CIDR"|"IP-CIDR6") {
        return None;
    }
    Some(json!({"kind":kind,"value":name,"route":route,"enabled":!disabled,"folder":""}))
}

fn describe(node: &Value) -> Value {
    let name = text(node,"name");
    let kind = text(node,"type");
    let flag = name.chars().collect::<Vec<_>>().windows(2).find_map(|pair| {
        if pair.iter().all(|character| ('\u{1f1e6}'..='\u{1f1ff}').contains(character)) {
            Some(pair.iter().map(|character| (b'A' + (*character as u32 - 0x1f1e6) as u8) as char).collect::<String>())
        } else { None }
    });
    let code = flag.unwrap_or_else(|| {
        let lower = name.to_lowercase();
        [("авто","AUTO"),("германи","DE"),("финлянди","FI"),("швеци","SE"),("польш","PL"),("нидерланд","NL"),
            ("литв","LT"),("чехи","CZ"),("итали","IT"),("швейцари","CH"),("болгари","BG"),("эстони","EE"),
            ("росси","RU"),("америк","US")].into_iter().find(|(key,_)| lower.contains(key))
            .map_or("",|(_,code)| code).to_owned()
    });
    let label = match kind {
        "xray-profile" => "XRAY / АВТО / JSON",
        "xray" if name.contains("Hysteria") => "HYSTERIA2 / QUIC / JSON",
        "xray" => "XRAY / JSON",
        _ => kind,
    };
    json!({"name":name,"title":name,"details":label.to_uppercase(),"countryCode":code})
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io;

    #[test]
    fn vpn_batch_switches_to_one_probe_after_socket_shortage() {
        let state = Mutex::new(Measurements::default());
        let simultaneous = std::sync::Barrier::new(2);
        let first_attempt = AtomicBool::new(true);
        let serial = Mutex::new(Vec::new());
        ping_vpn_batch(["a", "b", "c", "d"].into_iter(), &AtomicBool::new(false), &state, |name| {
            if name == "b" || (name == "a" && first_attempt.swap(false, Ordering::AcqRel)) {
                simultaneous.wait();
                if name == "a" { return Err(io::Error::from_raw_os_error(10055).into()); }
            } else {
                serial.lock().unwrap().push(name.to_owned());
                thread::sleep(Duration::from_millis(10));
            }
            Ok(PingResult::Measured(42))
        });
        assert_eq!(*serial.lock().unwrap(), ["a", "c", "d"]);
        assert_eq!(state.lock().unwrap().latest.len(), 4);
    }

    #[test]
    fn keeps_each_servers_last_success_across_methods() {
        let mut measurements = Measurements::default();
        measurements.record("one", Method::Vpn, PingResult::Measured(97));
        measurements.record("two", Method::Vpn, PingResult::Measured(43));
        measurements.method = Method::Icmp;
        measurements.latest.clear();
        measurements.record("one", Method::Icmp, PingResult::NoResponse);
        assert!(matches!(measurements.last_success.get("one"), Some((97, Method::Vpn))));
        measurements.record("one", Method::Icmp, PingResult::Measured(24));
        assert!(matches!(measurements.last_success.get("one"), Some((24, Method::Icmp))));
        assert!(matches!(measurements.last_success.get("two"), Some((43, Method::Vpn))));
    }

    #[test]
    fn retries_one_socket_shortage_and_keeps_the_result() {
        let stop = AtomicBool::new(false);
        let mut calls = 0;
        let delay = retry_ping(&stop, Duration::from_millis(1), || {
            calls += 1;
            if calls == 1 { return Err(io::Error::from_raw_os_error(10055).into()); }
            Ok(42)
        }).unwrap();
        assert_eq!((calls, delay), (2, 42));
    }

    #[test]
    fn persistent_socket_shortage_pauses_the_batch_without_overwriting_completed_results() {
        let stop = AtomicBool::new(false);
        let mut calls = 0;
        let error = retry_ping::<()>(&stop, Duration::ZERO, || {
            calls += 1;
            Err(io::Error::from_raw_os_error(10055).into())
        }).unwrap_err();
        assert_eq!(calls, 2);
        let mut measurements = Measurements::default();
        measurements.latest.insert("server".to_owned(), PingResult::Measured(42));
        measurements.record_issue(error.as_ref());
        assert!(measurements.retry_after.is_some_and(|until| until > Instant::now()));
        assert!(matches!(measurements.latest.get("server"), Some(PingResult::Measured(42))));
    }

    #[test]
    fn a_cancelled_batch_does_not_retry() {
        let stop = AtomicBool::new(false);
        let mut calls = 0;
        let error = retry_ping::<()>(&stop, PING_RETRY_DELAY, || {
            calls += 1;
            stop.store(true, Ordering::Release);
            Err(io::Error::from_raw_os_error(10055).into())
        }).unwrap_err();
        assert!(socket_exhausted(error.as_ref()));
        assert_eq!(calls, 1);
    }
}
