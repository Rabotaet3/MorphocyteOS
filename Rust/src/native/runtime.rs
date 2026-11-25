use super::{Result, problem, random_token, http_agent, socket_exhausted};
use super::profile::{Profile, decoded_text, is_section, list, text};
use super::speed::{self, Channel};
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    fs,
    io::{self, BufRead, BufReader, Read},
    net::TcpListener,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{Arc, atomic::{AtomicBool, Ordering}},
    thread,
    time::{Duration, Instant},
};
use std::os::windows::io::OwnedHandle;

const NO_WINDOW: u32 = 0x0800_0000;
const XRAY_PROBE_USER: &str = "morphocyte-ping";
const PROBE_SITES: [(&str, &str); 3] = [("www.gstatic.com", "/generate_204"),
    ("cp.cloudflare.com", "/generate_204"), ("example.com", "/")];

pub(crate) struct Engine {
    _job: OwnedHandle,
    child: Child,
    name: String,
}

impl Drop for Engine {
    fn drop(&mut self) {
        if !matches!(self.child.try_wait(), Ok(Some(_))) { let _ = self.child.kill(); }
        let _ = self.child.wait();
    }
}

pub(crate) struct Session {
    engines: Vec<Engine>,
    directory: PathBuf,
    controller: Controller,
    speed: Option<Channel>,
    probe: Option<Arc<Probe>>,
}

#[derive(Clone)]
pub(crate) struct Controller {
    port: u16,
    secret: String,
    agent: ureq::Agent,
}

pub(crate) struct Probe {
    port: u16,
    password: String,
    users: BTreeMap<String, String>,
}

#[derive(Debug)]
pub(crate) struct ProbeUnavailable;

impl std::fmt::Display for ProbeUnavailable {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("Проверочные сайты не ответили на HTTPS GET через выбранный сервер.")
    }
}

impl std::error::Error for ProbeUnavailable {}

impl Probe {
    pub(crate) fn latency(&self, tag: &str, stopped: Option<&AtomicBool>) -> Result<i64> {
        let user = self.users.get(tag).ok_or_else(|| problem("Профиль сервера отсутствует в ядре."))?;
        let proxy = ureq::Proxy::new(&format!("http://{user}:{}@127.0.0.1:{}", self.password, self.port))?;
        let agent = ureq::Agent::new_with_config(ureq::config::Config::builder()
            .proxy(Some(proxy)).max_redirects(0).timeout_global(Some(Duration::from_secs(6)))
            .tls_config(ureq::tls::TlsConfig::builder().provider(ureq::tls::TlsProvider::NativeTls)
                .root_certs(ureq::tls::RootCerts::PlatformVerifier).build()).build());
        for (host, path) in PROBE_SITES {
            if stopped.is_some_and(|flag| flag.load(Ordering::Acquire)) {
                return Err(io::Error::from(io::ErrorKind::Interrupted).into());
            }
            let start = Instant::now();
            match agent.get(format!("https://{host}{path}")).header("Cache-Control", "no-cache").call() {
                Ok(response) if response.status().is_success() => return Ok(start.elapsed().as_millis() as i64),
                Err(error) if socket_exhausted(&error) || matches!(&error, ureq::Error::Io(cause)
                    if cause.kind() == io::ErrorKind::ConnectionRefused) => return Err(error.into()),
                _ => {}
            }
        }
        Err(ProbeUnavailable.into())
    }

    fn append_routes(&self, inbounds: &mut Vec<Value>, rules: &mut Vec<Value>, probe_routes: &BTreeMap<String, String>) {
        let accounts = self.users.values().map(|user| json!({"username":user,"password":self.password})).collect::<Vec<_>>();
        inbounds.push(json!({"type":"http","tag":"atlas-probe","listen":"127.0.0.1","listen_port":self.port,"users":accounts}));
        let domains = PROBE_SITES.map(|(host, _)| host);
        for (tag, user) in &self.users {
            rules.push(json!({"inbound":["atlas-probe"],"auth_user":[user],"domain":domains,"port":[443],
                "action":"route","outbound":probe_routes.get(tag).unwrap_or(tag)}));
        }
        rules.push(json!({"inbound":["atlas-probe"],"action":"reject"}));
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.engines.clear();
        if let Ok(entries) = fs::read_dir(&self.directory) {
            for entry in entries.flatten().filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_file())) {
                let _ = fs::remove_file(entry.path());
            }
        }
        let _ = fs::remove_dir(&self.directory);
    }
}

fn bind_port() -> Result<TcpListener> { Ok(TcpListener::bind(("127.0.0.1", 0))?) }
fn port(listener: &TcpListener) -> Result<u16> { Ok(listener.local_addr()?.port()) }

enum LocalPorts {
    Live(Vec<TcpListener>),
    Validation(u16),
}

impl LocalPorts {
    fn reserve(&mut self) -> Result<u16> {
        match self {
            Self::Live(listeners) => {
                let listener = bind_port()?;
                let number = port(&listener)?;
                listeners.push(listener);
                Ok(number)
            }
            Self::Validation(next) => {
                let number = *next;
                *next = number.checked_add(1).ok_or_else(|| problem("Слишком много локальных входов в профиле."))?;
                Ok(number)
            }
        }
    }
}

fn xray_guard(value: &Value) -> Result<()> {
    match value {
        Value::Object(entries) => for (name, member) in entries {
            if matches!(name.as_str(), "certificateFile" | "keyFile" | "masterKeyLog" | "webhook" | "script" |
                "api" | "reverse" | "metrics" | "fakedns") || (name == "allowInsecure" && member == true)
            {
                return Err(problem("В JSON Xray есть неподдерживаемые управляющие параметры."));
            }
            xray_guard(member)?;
        },
        Value::Array(entries) => for member in entries { xray_guard(member)?; },
        Value::String(value) if value.trim_start_matches('!').to_ascii_lowercase().starts_with("ext:") =>
            return Err(problem("Внешние файлы в профиле Xray не поддерживаются.")),
        _ => {}
    }
    Ok(())
}

fn xray_listener(port: u16, pass: &str, tag: &str) -> Value {
    json!({"tag":tag, "listen":"127.0.0.1", "port":port, "protocol":"socks",
        "settings":{"auth":"password","accounts":[{"user":"morphocyte","pass":pass}],"udp":true,"ip":"127.0.0.1"}})
}

fn xray_link(source: &str) -> Result<Value> {
    let invalid = || problem("Некорректные или неподдерживаемые параметры ссылки Xray.");
    let url = url::Url::parse(source).map_err(|_| invalid())?;
    let hysteria = matches!(url.scheme(), "hysteria2" | "hy2");
    if !hysteria && url.scheme() != "vless" { return Err(invalid()); }
    let host = url.host_str().ok_or_else(invalid)?.trim_matches(['[',']']);
    let port = url.port().or(hysteria.then_some(443)).ok_or_else(invalid)?;
    let identity = decoded_text(url.username());
    if identity.is_empty() || identity.chars().any(char::is_control) || url.password().is_some() { return Err(invalid()); }
    let mut options = std::collections::HashMap::new();
    for (key, value) in url.query_pairs() {
        if options.insert(key.to_ascii_lowercase(), value).is_some() { return Err(invalid()); }
    }
    let allowed: &[&str] = if hysteria { &["sni","insecure","alpn","obfs","obfs-password","fm","pcs"] }
        else { &["encryption","security","sni","allowinsecure","insecure","type","host","path","flow","fp","alpn","pbk",
            "sid","servicename","mode","headertype","fm","pcs","extra"] };
    if options.keys().any(|name| !allowed.contains(&name.as_str())) { return Err(invalid()); }
    let get = |key: &str| options.get(key).map_or("", AsRef::as_ref);
    let default = |key: &str, fallback| options.get(key).map_or(fallback, AsRef::as_ref);
    if ["insecure","allowinsecure"].iter().any(|key| options.contains_key(*key) && !matches!(get(key),"0"|"false")) {
        return Err(invalid());
    }
    let network = if hysteria { "hysteria" } else { default("type","tcp") };
    if !matches!(network,"tcp"|"raw"|"ws"|"grpc"|"httpupgrade"|"xhttp"|"splithttp"|"hysteria") { return Err(invalid()); }
    let security = if hysteria { "tls" } else { default("security","none") };
    if !matches!(security,"none"|"tls"|"reality") || !matches!(default("encryption","none"),"none") {
        return Err(invalid());
    }
    let mut stream = json!({"network":network, "security":security});
    let (protocol, settings) = if hysteria {
        stream["hysteriaSettings"] = json!({"version":2,"auth":identity});
        ("hysteria",json!({"version":2,"address":host,"port":port}))
    } else {
        ("vless",json!({"vnext":[{"address":host,"port":port,"users":[{"id":identity,"encryption":"none","flow":get("flow")}]}]}))
    };
    if security != "none" {
        let mut tls = json!({"serverName":default("sni",host)});
        if !hysteria || options.contains_key("fp") { tls["fingerprint"] = json!(default("fp","chrome")); }
        if !get("alpn").is_empty() { tls["alpn"] = json!(get("alpn").split(',').collect::<Vec<_>>()); }
        if security == "tls" {
            if let Some(pin) = options.get("pcs") {
                if pin.len() != 64 || !pin.bytes().all(|byte| byte.is_ascii_hexdigit()) { return Err(invalid()); }
                tls["pinnedPeerCertSha256"] = json!(pin);
            }
            stream["tlsSettings"] = tls;
        } else {
            if options.contains_key("pcs") || options.contains_key("alpn") { return Err(invalid()); }
            tls["publicKey"] = json!(get("pbk"));
            tls["shortId"] = json!(get("sid"));
            stream["realitySettings"] = tls;
        }
    } else if options.contains_key("pcs") { return Err(invalid()); }
    if let Some(value) = options.get("fm") {
        let mask: Value = serde_json::from_str(value).map_err(|_| invalid())?;
        let object = mask.as_object().ok_or_else(invalid)?;
        if object.keys().any(|key| !matches!(key.as_str(),"tcp"|"udp"|"quicParams")) { return Err(invalid()); }
        xray_guard(&mask)?;
        stream["finalmask"] = mask;
    }
    if hysteria && !get("obfs").is_empty() {
        if get("obfs") != "salamander" || get("obfs-password").is_empty() { return Err(invalid()); }
        if stream.get("finalmask").is_none() { stream["finalmask"] = json!({}); }
        if stream["finalmask"].get("udp").is_none() { stream["finalmask"]["udp"] = json!([]); }
        stream["finalmask"]["udp"].as_array_mut().ok_or_else(invalid)?
            .push(json!({"type":"salamander","settings":{"password":get("obfs-password")}}));
    }
    match network {
        "xhttp" | "splithttp" => {
            let mut transport = json!({"host":get("host"),"path":default("path","/"),"mode":default("mode","auto")});
            if let Some(extra) = options.get("extra") {
                transport["extra"] = serde_json::from_str(extra).map_err(|_| invalid())?;
                xray_guard(&transport["extra"])?;
            }
            stream["xhttpSettings"] = transport;
        }
        "grpc" => stream["grpcSettings"] = json!({"serviceName":get("servicename"),"multiMode":get("mode")=="multi"}),
        "ws" | "httpupgrade" => {
            let key = if network == "ws" { "wsSettings" } else { "httpupgradeSettings" };
            stream[key] = json!({"host":get("host"),"path":default("path","/")});
        }
        _ => {}
    }
    if options.contains_key("extra") && !matches!(network,"xhttp"|"splithttp") { return Err(invalid()); }
    Ok(json!({"protocol":protocol,"streamSettings":stream,"settings":settings}))
}

fn xray_full(mut value: Value, port: u16, pass: &str) -> Result<Value> {
    xray_guard(&value)?;
    if !value.is_object() || list(&value, "outbounds").is_empty() || list(&value, "outbounds").len() > 64 {
        return Err(problem("Полный профиль Xray не содержит поддерживаемой группы подключений."));
    }
    for node in list(&value, "outbounds") {
        if !matches!(text(node, "protocol"), "vless" | "hysteria" | "freedom" | "blackhole") {
            return Err(problem("Тип одного из выходов JSON Xray пока не поддерживается."));
        }
    }
    let tags = list(&value, "inbounds").iter().map(|node| text(node, "tag").to_owned()).collect::<Vec<_>>();
    let sniffing = list(&value, "inbounds").iter().find(|node| text(node, "protocol") == "socks")
        .and_then(|node| node.get("sniffing")).cloned();
    let name = "morphocyte-profile-in";
    let mut inbound = xray_listener(port, pass, name);
    if let Some(sniffing) = sniffing { inbound["sniffing"] = sniffing; }
    value["inbounds"] = json!([inbound]);
    if let Some(rules) = value.get_mut("routing").and_then(|routing| routing.get_mut("rules")).and_then(Value::as_array_mut) {
        for rule in rules {
            if let Some(previous) = rule.get("inboundTag") {
                if previous.as_array().is_none_or(|items| items.iter().any(|item|
                    item.as_str().is_none_or(|tag| !tags.iter().any(|known| known == tag)))) {
                    return Err(problem("Неизвестный вход в маршрутах полного JSON Xray."));
                }
                rule["inboundTag"] = json!([name]);
            }
            if let Some(addresses) = rule.get_mut("ip").and_then(Value::as_array_mut) {
                const PRIVATE: [&str; 18] = ["0.0.0.0/8","10.0.0.0/8","100.64.0.0/10","127.0.0.0/8",
                    "169.254.0.0/16","172.16.0.0/12","192.0.0.0/24","192.0.2.0/24","192.88.99.0/24","192.168.0.0/16",
                    "198.18.0.0/15","198.51.100.0/24","203.0.113.0/24","224.0.0.0/3","::/127","fc00::/7","fe80::/10","ff00::/8"];
                *addresses = addresses.iter().flat_map(|item| {
                    if item == "geoip:private" { PRIVATE.into_iter().map(|range| json!(range)).collect() }
                    else { vec![item.clone()] }
                }).collect();
            }
        }
    }
    let object = value.as_object_mut().expect("validated object");
    for key in ["remarks", "meta", "stats"] { object.remove(key); }
    value["log"] = json!({"loglevel":"none"});
    Ok(value)
}

fn add_xray_probe_route(config: &mut Value, password: &str) -> bool {
    let mut peers = list(config, "outbounds").iter()
        .filter(|node| matches!(text(node, "protocol"), "vless" | "hysteria"));
    let Some(peer) = peers.next() else { return false; };
    let tag = text(peer, "tag").to_owned();
    if tag.is_empty() || peers.next().is_some() { return false; }

    let inbound = config["inbounds"][0]["tag"].clone();
    if config.get("routing").is_none() { config["routing"] = json!({}); }
    if !config["routing"].is_object() { return false; }
    if config["routing"].get("rules").is_none() { config["routing"]["rules"] = json!([]); }
    let Some(rules) = config["routing"]["rules"].as_array_mut() else { return false; };
    rules.insert(0, json!({"type":"field","inboundTag":[inbound],"user":[XRAY_PROBE_USER],"outboundTag":tag}));
    config["inbounds"][0]["settings"]["accounts"].as_array_mut().expect("generated SOCKS accounts")
        .push(json!({"user":XRAY_PROBE_USER,"pass":password}));
    true
}

fn configure_xray_dns(config: &mut Value, dns_port: u16, uplink: Option<&str>) -> Result<()> {
    if config.get("dns").is_none() {
        config["dns"] = json!({"servers":[format!("tcp+local://127.0.0.1:{dns_port}")],"queryStrategy":"UseIP"});
    }
    let outbounds = config["outbounds"].as_array_mut().ok_or_else(|| problem("Неверный список выходов Xray."))?;
    for node in outbounds {
        if !matches!(text(node, "protocol"), "vless" | "hysteria") { continue; }
        if node.get("streamSettings").is_none() { node["streamSettings"] = json!({}); }
        let stream = node["streamSettings"].as_object_mut().ok_or_else(|| problem("Некорректный транспорт Xray."))?;
        let socket = stream.entry("sockopt").or_insert_with(|| json!({})).as_object_mut()
            .ok_or_else(|| problem("Некорректные параметры сокета Xray."))?;
        socket.entry("domainStrategy").or_insert_with(|| json!("ForceIP"));
        if let Some(interface) = uplink { socket.entry("interface").or_insert_with(|| json!(interface)); }
    }
    Ok(())
}

fn sing_proxy(node: &Value) -> Result<Value> {
    let input = text(node, "type");
    let kind = match input {
        "socks5" => "socks", "ss" => "shadowsocks",
        "vless" | "vmess" | "trojan" | "http" | "direct" | "hysteria2" => input,
        _ => return Err(problem(format!("Неподдерживаемый тип VPN-профиля: {input}."))),
    };
    let mut output = json!({"type":kind,"tag":node["name"]});
    if kind == "direct" { return Ok(output); }
    let port = node["port"].as_f64().or_else(|| text(node, "port").parse().ok())
        .filter(|number| number.fract() == 0.0 && (1.0..=65535.0).contains(number))
        .ok_or_else(|| problem(format!("Профиль {kind} содержит некорректный порт VPN-сервера.")))? as u16;
    let address = match &node["server"] {
        Value::String(address) if !address.is_empty() => address.clone(),
        Value::Number(address) if address.as_u64().is_some() => address.to_string(),
        _ => return Err(problem("В профиле отсутствует адрес VPN-сервера.")),
    };
    output["server"] = json!(address);
    output["server_port"] = json!(port);
    for (key, alias) in [("password","password"),("username","username"),("uuid","uuid"),("flow","flow"),("packet-encoding","packet_encoding")] {
        if let Some(value) = node.get(key).filter(|value| !value.is_null()) { output[alias] = value.clone(); }
    }
    if kind == "socks" { output["version"] = json!("5"); }
    if kind == "shadowsocks" { output["method"] = node["cipher"].clone(); }
    if kind == "vmess" {
        output["alter_id"] = json!(node["alterId"].as_u64().unwrap_or(0));
        output["security"] = json!(node["security"].as_str().or(node["cipher"].as_str()).unwrap_or("auto"));
    }
    if kind == "hysteria2" && !text(node, "obfs").is_empty() {
        output["obfs"] = json!({"type":node["obfs"], "password":node["obfs-password"]});
    }
    if node["tls"] == true || matches!(kind, "trojan" | "hysteria2") {
        let mut tls = json!({"enabled":true,"insecure":node["skip-cert-verify"] == true});
        if let Some(host) = node["servername"].as_str().or(node["sni"].as_str()) { tls["server_name"] = json!(host); }
        if node["alpn"].is_array() { tls["alpn"] = node["alpn"].clone(); }
        if let Some(fingerprint) = node["client-fingerprint"].as_str() {
            tls["utls"] = json!({"enabled":true,"fingerprint":fingerprint});
        }
        if let Some(reality) = node.get("reality-opts") {
            tls["reality"] = json!({"enabled":true,"public_key":reality["public-key"],"short_id":reality["short-id"]});
        }
        output["tls"] = tls;
    }
    match text(node, "network") {
        "" | "tcp" | "raw" => {}
        "ws" => {
            let options = &node["ws-opts"];
            let mut transport = json!({"type":"ws","path":options["path"].as_str().unwrap_or("/")});
            if options.get("headers").is_some() { transport["headers"] = options["headers"].clone(); }
            if let Some(length) = options["max-early-data"].as_u64().filter(|length| *length > 0) {
                transport["max_early_data"] = json!(length);
                transport["early_data_header_name"] = json!(options["early-data-header-name"].as_str().unwrap_or("Sec-WebSocket-Protocol"));
            }
            output["transport"] = transport;
        }
        "grpc" => output["transport"] = json!({"type":"grpc","service_name":text(&node["grpc-opts"], "grpc-service-name")}),
        _ => return Err(problem("Транспорт профиля пока требуется запускать через JSON Xray.")),
    }
    Ok(output)
}

fn rule_target(target: &str) -> Value {
    if matches!(target, "REJECT" | "REJECT-DROP") { json!({"action":"reject"}) }
    else { json!({"action":"route","outbound":target}) }
}

fn convert_rules(profile: &Profile, full_tunnel: bool, vpn: &str) -> Result<(Vec<Value>, String)> {
    let mut rules = Vec::new();
    let mut final_route = if full_tunnel { vpn } else { "DIRECT" }.to_owned();
    for entry in list(&profile.data, "rules") {
        let rule = entry.as_str().ok_or_else(|| problem("Некорректное правило маршрутизации."))?;
        if rule.trim_start().starts_with('#') { continue; }
        let parts = rule.split(',').map(str::trim).collect::<Vec<_>>();
        if let ["MATCH" | "FINAL", target] = parts.as_slice() {
            if !full_tunnel { final_route = if *target == "VPN" { vpn } else { target }.to_owned(); }
            break;
        }
        if parts.len() < 3 { return Err(problem("Неполное правило маршрутизации.")); }
        let field = match parts[0] {
            "DOMAIN" => "domain", "DOMAIN-SUFFIX" => "domain_suffix", "DOMAIN-KEYWORD" => "domain_keyword",
            "PROCESS-NAME" => "process_name", "PROCESS-PATH" => "process_path",
            "IP-CIDR" | "IP-CIDR6" => "ip_cidr", "SRC-IP-CIDR" => "source_ip_cidr",
            _ => return Err(problem(format!("Тип правила \"{}\" ещё не перенесён.", parts[0]))),
        };
        if !full_tunnel || matches!(parts[2], "DIRECT" | "REJECT" | "REJECT-DROP") {
            let mut converted = rule_target(if parts[2] == "VPN" { vpn } else { parts[2] });
            converted[field] = json!([parts[1]]);
            rules.push(converted);
        }
    }
    if matches!(final_route.as_str(), "REJECT" | "REJECT-DROP") {
        rules.push(rule_target(&final_route));
        final_route = "DIRECT".to_owned();
    }
    Ok((rules, final_route))
}

impl Session {
    pub(crate) fn start(root: &Path, profile: &Profile, selected: &str, tunnel: bool, full_tunnel: bool) -> Result<Self> {
        Self::prepare(root,profile,selected,tunnel,full_tunnel,true)
    }

    pub(crate) fn validate_tunnel(root: &Path, profile: &Profile) -> Result<()> {
        Self::prepare(root,profile,"",true,false,false)?;
        Ok(())
    }

    fn prepare(root: &Path, profile: &Profile, selected: &str, tunnel: bool, full_tunnel: bool, start: bool) -> Result<Self> {
        let secret = random_token()?;
        let probe_tag_prefix = format!("atlas-test-{}-", &random_token()?[..16]);
        let directory = root.join(".runtime-native").join(&secret[..24]);
        let mut ports = if start { LocalPorts::Live(Vec::new()) } else { LocalPorts::Validation(20_000) };
        let api = Controller::new(ports.reserve()?, secret);
        let speed = if tunnel { Some(Channel { port: ports.reserve()?, password: random_token()? }) } else { None };
        let servers = profile.servers();
        let probe = if servers.is_empty() { None } else {
            let users = servers.iter().enumerate().map(|(index, node)| (text(node,"name").to_owned(), format!("p{index}"))).collect();
            Some(Arc::new(Probe { port: ports.reserve()?, password: random_token()?, users }))
        };
        fs::create_dir_all(&directory)?;
        let mut session = Self { engines: vec![], directory, controller: api, speed, probe };
        let mut outbounds = vec![json!({"tag":"DIRECT", "type":"direct"})];
        let mut xray_outbounds = Vec::new();
        let mut xray_inbounds = Vec::new();
        let mut xray_rules = Vec::new();
        let mut xray_configs = Vec::new();
        let mut probe_routes = BTreeMap::new();
        let mut ignored = Vec::new();
        for node in list(&profile.data, "proxies") {
            if node.get("morphocyte-section").is_some() || is_section(node) || text(node, "type") == "reject" {
                ignored.push(text(node, "name"));
                continue;
            }
            let kind = text(node, "type");
            if matches!(kind, "xray" | "xray-profile") {
                let local = ports.reserve()?;
                let pass = random_token()?;
                if kind == "xray-profile" {
                    let mut full = xray_full(serde_json::from_str(text(node, "xray-json")).map_err(|_| problem("Некорректный JSON Xray."))?, local, &pass)?;
                    if add_xray_probe_route(&mut full, &pass) {
                        let tag = format!("{probe_tag_prefix}{}", probe_routes.len());
                        probe_routes.insert(text(node, "name").to_owned(), tag.clone());
                        outbounds.push(json!({"type":"socks","tag":tag,"server":"127.0.0.1",
                            "server_port":local,"version":"5","username":XRAY_PROBE_USER,"password":pass}));
                    }
                    xray_configs.push(full);
                } else {
                    let mut outbound: Value = if let Some(raw) = node["xray-outbound"].as_str() {
                        serde_json::from_str(raw).map_err(|_| problem("Некорректный выход Xray."))?
                    } else if node["xray-outbound"].is_object() {
                        node["xray-outbound"].clone()
                    } else {
                        xray_link(text(node, "xray-link"))?
                    };
                    xray_guard(&outbound)?;
                    if !matches!(text(&outbound, "protocol"), "vless" | "hysteria") {
                        return Err(problem("Неподдерживаемый выход Xray."));
                    }
                    let tag = format!("atlas-connection-{}", xray_outbounds.len() + 1);
                    outbound["tag"] = json!(tag);
                    xray_rules.push(json!({"type":"field","inboundTag":[tag],"outboundTag":tag}));
                    xray_inbounds.push(xray_listener(local, &pass, &tag));
                    xray_outbounds.push(outbound);
                }
                outbounds.push(json!({"type":"socks","tag":node["name"],"server":"127.0.0.1",
                    "server_port":local,"version":"5","username":"morphocyte","password":pass}));
            } else {
                outbounds.push(sing_proxy(node)?);
            }
        }
        if !xray_outbounds.is_empty() {
            xray_configs.insert(0, json!({"log":{"loglevel":"none"},"inbounds":xray_inbounds,"outbounds":xray_outbounds,
                "routing":{"domainStrategy":"AsIs","rules":xray_rules}}));
        }
        let groups = list(&profile.data, "proxy-groups");
        for group in groups {
            let kind = text(group, "type");
            let mut members = list(group, "proxies").iter().filter(|member| {
                member.as_str().is_some_and(|name| !matches!(name, "REJECT" | "REJECT-DROP") && !ignored.contains(&name))
            }).cloned().collect::<Vec<_>>();
            if members.is_empty() { members.push(json!("DIRECT")); }
            let group = match kind {
                "select" => {
                    let choice = members.iter().find(|name| name.as_str() == Some(selected)).unwrap_or(&members[0]);
                    json!({"tag":group["name"],"type":"selector","outbounds":members,"default":choice,
                        "interrupt_exist_connections":true})
                }
                "url-test" => json!({"tag":group["name"],"type":"urltest","outbounds":members,
                    "url":group["url"].as_str().unwrap_or("https://www.gstatic.com/generate_204"),
                    "interval":format!("{}s",group["interval"].as_u64().unwrap_or(300).max(10))}),
                _ => return Err(problem("Этот тип группы серверов ещё не перенесён.")),
            };
            outbounds.push(group);
        }
        let first = servers.first().map(|node| text(node, "name")).unwrap_or("DIRECT");
        let selected = if selected.is_empty() { first } else { selected };
        let vpn = if groups.iter().any(|group| text(group, "name") == "VPN") { "VPN" }
            else { groups.first().map(|group| text(group, "name")).unwrap_or(selected) };
        let (custom, final_route) = if tunnel { convert_rules(profile, full_tunnel, vpn)? } else { (vec![], "DIRECT".into()) };
        let dns_port = (!xray_configs.is_empty()).then(|| ports.reserve()).transpose()?;
        let mut inbounds = Vec::new();
        let mut rules = Vec::new();
        if let Some(port) = dns_port {
            inbounds.push(json!({"type":"direct","tag":"atlas-bootstrap","listen":"127.0.0.1","listen_port":port}));
            rules.push(json!({"inbound":["atlas-bootstrap"],"action":"hijack-dns"}));
        }
        if let Some(channel) = &session.speed {
            inbounds.push(json!({"type":"http","tag":"atlas-speed","listen":"127.0.0.1","listen_port":channel.port,
                "users":[{"username":"morphocyte","password":channel.password}]}));
            rules.push(json!({"inbound":["atlas-speed"],"domain":speed::HOSTS,"port":[443],
                "action":"route","outbound":selected}));
            rules.push(json!({"inbound":["atlas-speed"],"action":"reject"}));
        }
        if let Some(probe) = &session.probe { probe.append_routes(&mut inbounds, &mut rules, &probe_routes); }
        if tunnel {
            inbounds.push(json!({"type":"tun","tag":"atlas-tun","interface_name":"MorphocyteTUN",
                "address":["172.19.0.1/30","fdfe:dcba:9876::1/126"],"auto_route":true,"strict_route":true,
                "dns_mode":"hijack","stack":"gvisor","mtu":1280}));
            rules.push(json!({"action":"sniff"}));
            rules.push(json!({"protocol":"dns","action":"hijack-dns"}));
        }
        let base_rule_count = rules.len();
        rules.extend(custom);
        let mut config = json!({"log":{"level":"warn"},"inbounds":inbounds,"outbounds":outbounds,
            "dns":{"servers":[{"type":"udp","tag":"atlas-resolver","server":"1.1.1.1"}],"final":"atlas-resolver"},
            "route":{"rules":rules,"final":final_route,"auto_detect_interface":tunnel,"default_domain_resolver":"atlas-resolver"},
            "experimental":{"clash_api":{"external_controller":format!("127.0.0.1:{}",session.controller.port),"secret":session.controller.secret}}});
        let uplink = if tunnel && !xray_configs.is_empty() { super::physical_uplink() } else { None };
        if let Some(ref interface) = uplink {
            config["route"]["auto_detect_interface"] = json!(false);
            config["route"]["default_interface"] = json!(interface);
            let local = super::loopback().ok_or_else(|| problem("Не найден петлевой интерфейс для Xray."))?;
            for proxy in config["outbounds"].as_array_mut().expect("generated array") {
                if text(proxy, "type") == "socks" && text(proxy, "server") == "127.0.0.1" {
                    proxy["bind_interface"] = json!(local);
                }
            }
        }
        let engines = root.join("ThirdPartySource").join("Engine");
        let singbox = engines.join("sing-box.exe");
        let xray = engines.join("xray.exe");
        let main = session.write_config("sing-box.json", &config)?;
        let mut bridge_paths = Vec::new();
        if let Some(dns_port) = dns_port {
            for (index, config) in xray_configs.iter_mut().enumerate() {
                configure_xray_dns(config, dns_port, uplink.as_deref())?;
                let path = session.write_config(&format!("xray-{index}.json"), config)?;
                bridge_paths.push(path);
            }
        }
        if !start {
            validate(&singbox, &["check","-c"], &main)?;
            for path in &bridge_paths { validate(&xray, &["run","-test","-config"], path)?; }

            // Both TUN modes use the same Xray configs. Only sing-box routing changes.
            let (custom, final_route) = convert_rules(profile, true, vpn)?;
            let rules = config["route"]["rules"].as_array_mut().expect("generated route rules");
            rules.truncate(base_rule_count);
            rules.extend(custom);
            config["route"]["final"] = json!(final_route);
            let main = session.write_config("sing-box.json", &config)?;
            validate(&singbox, &["check","-c"], &main)?;
            return Ok(session);
        }
        drop(ports);
        for path in &bridge_paths { session.engines.push(spawn(&xray, &["run","-config"], path)?); }
        session.engines.push(spawn(&singbox, &["run","-c"], &main)?);
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            let ready = session.api("version");
            for engine in &mut session.engines {
                if let Some(status) = engine.child.try_wait()? {
                    return Err(problem(format!("{} завершился при запуске VPN ({}).",
                        engine.name, status.code().unwrap_or_default())));
                }
            }
            match ready {
                Ok(_) => return Ok(session),
                Err(error) if socket_exhausted(error.as_ref()) => return Err(error),
                Err(_) => {}
            }
            if Instant::now() >= deadline {
                return Err(problem("Ядро не открыло локальный канал управления."));
            }
            thread::sleep(Duration::from_millis(40));
        }
    }

    fn write_config(&self, name: &str, config: &Value) -> Result<PathBuf> {
        let path = self.directory.join(name);
        fs::write(&path, serde_json::to_vec(config)?)?;
        Ok(path)
    }

    pub(crate) fn running(&mut self) -> bool {
        !self.engines.is_empty() && self.engines.iter_mut().all(|engine| matches!(engine.child.try_wait(), Ok(None)))
    }

    pub(crate) fn api(&self, path: &str) -> Result<Value> {
        self.controller.api(path)
    }

    pub(crate) fn latency(&self, tag: &str) -> Result<i64> {
        self.prober()?.latency(tag, None)
    }

    pub(crate) fn prober(&self) -> Result<Arc<Probe>> {
        self.probe.clone().ok_or_else(|| problem("В ядре нет серверов для проверки."))
    }

    pub(crate) fn controller(&self) -> Controller {
        self.controller.clone()
    }

    pub(super) fn speed_channel(&self) -> Option<&Channel> { self.speed.as_ref() }
}

impl Controller {
    fn new(port: u16, secret: String) -> Self {
        Self { port, secret, agent: http_agent(Duration::from_secs(5)) }
    }

    fn request(&self, path: &str) -> ureq::RequestBuilder<ureq::typestate::WithoutBody> {
        self.agent.get(format!("http://127.0.0.1:{}/{path}", self.port))
            .header("Authorization", format!("Bearer {}", self.secret))
    }

    pub(crate) fn api(&self, path: &str) -> Result<Value> {
        let mut response = self.request(path).call()?;
        Ok(response.body_mut().read_json()?)
    }

    pub(crate) fn stream_traffic(&self, stopped: &AtomicBool, mut sample: impl FnMut(u64, u64)) -> Result<()> {
        let mut response = self.request("traffic").config()
            .timeout_global(None)
            .timeout_connect(Some(Duration::from_secs(4)))
            .timeout_recv_response(Some(Duration::from_secs(4)))
            .timeout_per_read(Some(Duration::from_secs(4)))
            .timeout_per_write(Some(Duration::from_secs(4)))
            .build().call()?;
        let mut reader = BufReader::new(response.body_mut().with_config().limit(u64::MAX).reader());
        let mut line = Vec::with_capacity(256);
        while !stopped.load(Ordering::Acquire) {
            line.clear();
            if reader.by_ref().take(1024).read_until(b'\n', &mut line)? == 0 { break; }
            if !line.ends_with(b"\n") {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "Некорректный ответ счётчиков трафика.").into());
            }
            let data: Value = serde_json::from_slice(&line)?;
            if let (Some(receive), Some(send)) = (data["down"].as_u64(), data["up"].as_u64()) {
                sample(receive, send);
            }
        }
        Ok(())
    }

}

fn validate(executable: &Path, arguments: &[&str], config: &Path) -> Result<()> {
    let status = Command::new(executable).args(arguments).arg(config).creation_flags(NO_WINDOW)
        .stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).status()?;
    if !status.success() {
        return Err(problem(format!("{} отклонил сгенерированный профиль.", executable.file_name().unwrap_or_default().to_string_lossy())));
    }
    Ok(())
}

fn spawn(executable: &Path, arguments: &[&str], config: &Path) -> Result<Engine> {
    let mut child = Command::new(executable).args(arguments).arg(config).creation_flags(NO_WINDOW)
        .stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn()?;
    match crate::desktop::attach_backend(&child) {
        Ok(job) => Ok(Engine { _job: job, child, name: executable.file_name().unwrap_or_default().to_string_lossy().into_owned() }),
        Err(error) => {
            let _ = child.kill();
            let _ = child.wait();
            Err(error.into())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{io::Write, net::TcpStream};

    fn read_request(stream: &mut BufReader<TcpStream>, path: &str) {
        let mut line = String::new();
        stream.read_line(&mut line).unwrap();
        assert!(line.starts_with(&format!("GET /{path} ")), "{line}");
        loop {
            line.clear();
            assert!(stream.read_line(&mut line).unwrap() > 0);
            if line == "\r\n" { break; }
        }
    }

    #[test]
    fn traffic_stays_on_one_connection_beyond_the_normal_api_deadline() {
        let listener = bind_port().unwrap();
        let controller = Controller::new(port(&listener).unwrap(), "test-token".to_owned());
        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            let mut stream = BufReader::new(stream);
            read_request(&mut stream, "traffic");
            let stream = stream.get_mut();
            stream.write_all(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n").unwrap();
            for i in 0..10 {
                let chunk = format!("{{\"down\":{i},\"up\":{i}}}\n");
                write!(stream, "{:x}\r\n{chunk}\r\n", chunk.len()).unwrap();
                if i < 9 { thread::sleep(Duration::from_millis(625)); }
            }
        });
        let stopped = AtomicBool::new(false);
        let mut readings = Vec::new();
        let outcome = controller.stream_traffic(&stopped, |down, up| {
            assert_eq!(down, up);
            readings.push(down);
            if readings.len() == 10 { stopped.store(true, Ordering::Release); }
        });
        outcome.unwrap();
        assert_eq!(readings, (0..10).collect::<Vec<_>>());
        server.join().unwrap();
    }

    #[test]
    fn controller_clones_reuse_the_local_connection() {
        let listener = bind_port().unwrap();
        let controller = Controller::new(port(&listener).unwrap(), "test-token".to_owned());
        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            let mut stream = BufReader::new(stream);
            for path in ["version", "connections"] {
                read_request(&mut stream, path);
                stream.get_mut().write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}").unwrap();
            }
        });
        assert_eq!(controller.api("version").unwrap(), json!({}));
        assert_eq!(controller.clone().api("connections").unwrap(), json!({}));
        server.join().unwrap();
    }

    #[test]
    fn cancelled_ping_skips_remaining_checks() {
        let probe = Probe { port: 1, password: "test-token".to_owned(),
            users: BTreeMap::from([("server".to_owned(), "p0".to_owned())]) };
        let stopped = AtomicBool::new(true);
        let error = probe.latency("server", Some(&stopped)).unwrap_err();
        assert_eq!(error.downcast_ref::<io::Error>().unwrap().kind(), io::ErrorKind::Interrupted);
    }

    #[test]
    fn identifies_port_exhaustion_inside_the_http_error() {
        let error = io::Error::other(ureq::Error::Io(io::Error::from_raw_os_error(10055)));
        assert!(socket_exhausted(&error));
        assert!(!socket_exhausted(&io::Error::from_raw_os_error(10061)));
    }
}
