use super::{Result, problem};
use base64::{Engine, engine::general_purpose::{STANDARD, STANDARD_NO_PAD, URL_SAFE_NO_PAD}};
use serde_json::{Value, json};
use std::{collections::HashSet, path::Path};
use url::Url;

pub(crate) const MAX_PROFILE: usize = 8 * 1024 * 1024;

#[derive(Clone)]
pub(crate) struct Profile {
    pub(crate) data: Value,
}

pub(crate) fn text<'a>(node: &'a Value, field: &str) -> &'a str {
    node[field].as_str().unwrap_or_default()
}

pub(crate) fn list<'a>(node: &'a Value, field: &str) -> &'a [Value] {
    node[field].as_array().map_or(&[], Vec::as_slice)
}

pub(crate) fn is_section(node: &Value) -> bool {
    let name = text(node, "name");
    if !name.contains(['⬇', '↓']) { return false; }
    let host = &node["server"];
    if let Some(address) = host.as_u64() { return address <= 255; }
    host.as_str().is_some_and(|value| {
        value.parse::<u8>().is_ok() || value.parse::<std::net::Ipv4Addr>().is_ok_and(|ip| ip.octets()[..3] == [0, 0, 0])
    })
}

impl Profile {
    pub(crate) fn load(path: &Path) -> Result<Self> {
        if path.metadata()?.len() > MAX_PROFILE as u64 { return Err(problem("Профиль слишком большой.")); }
        Self::parse(&std::fs::read_to_string(path)?)
    }

    pub(crate) fn parse(input: &str) -> Result<Self> {
        let input = input.trim_start_matches('\u{feff}').trim();
        if input.is_empty() || input.len() > MAX_PROFILE {
            return Err(problem("Профиль пустой или превышает допустимый размер."));
        }
        let data = if let Ok(value) = serde_json::from_str::<Value>(input) {
            if value.get("proxies").is_some() {
                value
            } else {
                let entries = value.as_array().map_or(std::slice::from_ref(&value), Vec::as_slice);
                let nodes = entries.iter().enumerate().map(|(index, entry)| {
                    if !entry.is_object() || list(entry, "outbounds").is_empty() {
                        return Err(problem("JSON не содержит профиля Xray."));
                    }
                    let name = entry["remarks"].as_str().filter(|name| !name.trim().is_empty())
                        .map(str::to_owned).unwrap_or_else(|| format!("Xray {}", index + 1));
                    Ok(json!({"name": name, "type": "xray-profile", "xray-json": entry.to_string()}))
                }).collect::<Result<Vec<_>>>()?;
                Self::from_nodes(nodes)?
            }
        } else if let Ok(value) = serde_yaml_ng::from_str::<Value>(input)
            && value.is_object() && (value.get("proxies").is_some() || value.get("proxy-groups").is_some())
        {
            value
        } else {
            let packed = input.split_whitespace().collect::<String>();
            let decoded = [STANDARD, STANDARD_NO_PAD, URL_SAFE_NO_PAD].into_iter()
                .find_map(|codec| codec.decode(&packed).ok().and_then(|bytes| String::from_utf8(bytes).ok()));
            let lines = decoded.as_deref().filter(|value| value.contains("://")).unwrap_or(input);
            let nodes = lines.lines().map(str::trim).filter(|line| !line.is_empty() && !line.starts_with('#'))
                .map(parse_link).collect::<Result<Vec<_>>>()?;
            Self::from_nodes(nodes)?
        };
        let mut names = HashSet::new();
        for entry in list(&data, "proxies").iter().chain(list(&data, "proxy-groups")) {
            let name = text(entry, "name");
            if name.is_empty() || name.len() > 256 || name.chars().any(char::is_control)
                || matches!(name, "DIRECT" | "REJECT" | "REJECT-DROP" | "atlas-vpn") || !names.insert(name)
            {
                return Err(problem("Профиль содержит пустое, повторяющееся или зарезервированное имя сервера."));
            }
        }
        Ok(Self { data })
    }

    fn from_nodes(mut nodes: Vec<Value>) -> Result<Value> {
        if nodes.is_empty() { return Err(problem("В профиле не найдено серверов.")); }
        let mut names = HashSet::new();
        for node in &mut nodes {
            let base = text(node, "name").trim().to_owned();
            let base = if base.is_empty() || matches!(base.as_str(), "DIRECT" | "REJECT" | "REJECT-DROP" | "VPN" | "atlas-vpn") {
                "Сервер".to_owned()
            } else { base };
            let mut name = base.clone();
            for suffix in 2.. {
                if names.insert(name.clone()) { break; }
                name = format!("{base} ({suffix})");
            }
            node["name"] = json!(name);
        }
        let members = nodes.iter().map(|node| node["name"].clone()).collect::<Vec<_>>();
        Ok(json!({"proxies": nodes, "proxy-groups": [{"name": "VPN", "type": "select", "proxies": members}],
            "rules": ["MATCH,VPN"]}))
    }

    pub(crate) fn servers(&self) -> Vec<&Value> {
        list(&self.data, "proxies").iter().filter(|node| !is_section(node) && !matches!(text(node, "type"), "direct" | "reject")
            && node.get("morphocyte-section").is_none()).collect()
    }

    pub(crate) fn set_rules(&mut self, rules: &[Value]) {
        self.data["rules"] = json!(rules);
    }
}

pub(crate) fn decoded_text(value: &str) -> String {
    url::form_urlencoded::parse(value.replace('+', "%2B").as_bytes())
        .map(|(key, content)| if content.is_empty() { key.into_owned() } else { format!("{key}={content}") })
        .collect::<Vec<_>>().join("&")
}

fn parse_link(line: &str) -> Result<Value> {
    let link = Url::parse(line).map_err(|_| problem("Не удалось разобрать ссылку сервера."))?;
    let kind = match link.scheme() {
        "vless" => "vless", "trojan" => "trojan", "hysteria2" | "hy2" => "hysteria2",
        "socks5" => "socks5",
        _ => return Err(problem("Формат одной из ссылок пока не поддерживается Rust-модулем.")),
    };
    let name = link.fragment().map(decoded_text).unwrap_or_else(|| link.host_str().unwrap_or_default().to_owned());
    let query: std::collections::HashMap<_, _> = link.query_pairs().collect();
    let opt = |key: &str| query.get(key).map(AsRef::as_ref).unwrap_or_default();
    let mut node = json!({"type":kind, "name": name, "server": link.host_str(), "port": link.port().unwrap_or(443)});
    if matches!(kind, "hysteria2" | "vless")
        && (["fm","pcs","extra"].iter().any(|key| query.contains_key(*key)) ||
            matches!(opt("type"),"xhttp"|"splithttp"|"httpupgrade")) {
        node["type"] = json!("xray");
        node["xray-link"] = json!(line);
        return Ok(node);
    }
    if kind == "vless" {
        node["uuid"] = json!(link.username());
        if !opt("flow").is_empty() { node["flow"] = json!(opt("flow")); }
    } else if kind == "socks5" {
        node["username"] = json!(decoded_text(link.username()));
        node["password"] = json!(link.password().map(decoded_text));
    } else {
        node["password"] = json!(decoded_text(link.username()));
    }
    let security = opt("security");
    if matches!(security, "tls" | "reality") || matches!(kind, "trojan" | "hysteria2") {
        node["tls"] = json!(true);
        if !opt("sni").is_empty() { node["servername"] = json!(opt("sni")); }
        if !opt("fp").is_empty() { node["client-fingerprint"] = json!(opt("fp")); }
        if security == "reality" {
            node["reality-opts"] = json!({"public-key": opt("pbk"), "short-id": opt("sid")});
        }
    } else if !matches!(security, "" | "none") {
        return Err(problem("Неподдерживаемый режим TLS в ссылке."));
    }
    match opt("type") {
        "" | "tcp" | "raw" => {}
        "ws" => {
            node["network"] = json!("ws");
            node["ws-opts"] = json!({"path": opt("path")});
            if !opt("host").is_empty() { node["ws-opts"]["headers"] = json!({"Host":opt("host")}); }
        }
        "grpc" => {
            node["network"] = json!("grpc");
            node["grpc-opts"] = json!({"grpc-service-name": opt("serviceName")});
        }
        _ => return Err(problem("Транспорт этой ссылки пока требуется импортировать как JSON Xray.")),
    }
    if !opt("obfs").is_empty() {
        node["obfs"] = json!(opt("obfs"));
        node["obfs-password"] = json!(opt("obfs-password"));
    }
    Ok(node)
}
