use super::profile::{list, text};
use super::{Result, socket_exhausted};
use serde_json::Value;
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use windows::Win32::NetworkManagement::IpHelper::{
    ICMP_ECHO_REPLY, ICMPV6_ECHO_REPLY_LH, IP_SUCCESS, Icmp6CreateFile, Icmp6SendEcho2,
    IcmpCloseHandle, IcmpCreateFile, IcmpSendEcho,
};
use windows::Win32::Networking::WinSock::{AF_INET6, SOCKADDR_IN6};

#[derive(Clone, Copy, Default, PartialEq, Eq)]
pub(super) enum Method {
    #[default]
    Vpn,
    Tcp,
    Icmp,
}

impl Method {
    pub(super) fn key(self) -> &'static str {
        match self {
            Self::Vpn => "vpn",
            Self::Tcp => "tcp",
            Self::Icmp => "icmp",
        }
    }
}

pub(super) struct Endpoint {
    host: String,
    port: u16,
    udp_only: bool,
}

impl Endpoint {
    pub(super) fn from_node(node: &Value) -> Option<Self> {
        match text(node, "type") {
            "xray-profile" => {
                let config: Value = serde_json::from_str(text(node, "xray-json")).ok()?;
                let mut outbounds = list(&config, "outbounds").iter()
                    .filter(|outbound| matches!(text(outbound, "protocol"), "vless" | "hysteria"));
                let endpoint = Self::from_outbound(outbounds.next()?)?;
                outbounds.next().is_none().then_some(endpoint)
            }
            "xray" if node.get("xray-link").is_some() => {
                let link = url::Url::parse(text(node, "xray-link")).ok()?;
                let udp_only = matches!(link.scheme(), "hysteria2" | "hy2")
                    || link.query_pairs().any(|(key, value)| key == "alpn" && value == "h3");
                Self::new(link.host_str()?, link.port().or(udp_only.then_some(443))?, udp_only)
            }
            "xray" => match node["xray-outbound"].as_str() {
                Some(raw) => Self::from_outbound(&serde_json::from_str(raw).ok()?),
                None => Self::from_outbound(&node["xray-outbound"]),
            },
            kind => Self::from_fields(node, "server", kind == "hysteria2"),
        }
    }

    fn from_outbound(outbound: &Value) -> Option<Self> {
        let stream = &outbound["streamSettings"];
        let settings = &outbound["settings"];
        let tls_over_udp = matches!(text(stream, "network"), "quic" | "kcp")
            || stream["tlsSettings"]["alpn"].as_array().is_some_and(|values| values.as_slice() == ["h3"]);
        match text(outbound, "protocol") {
            "vless" => match list(settings, "vnext") {
                [peer] => Self::from_fields(peer, "address", tls_over_udp),
                _ => None,
            },
            "hysteria" => Self::from_fields(settings, "address", true),
            _ => None,
        }
    }

    fn from_fields(node: &Value, key: &str, udp_only: bool) -> Option<Self> {
        let port = node["port"].as_u64().or_else(|| node["port"].as_str()?.parse().ok())?;
        Self::new(node[key].as_str()?, u16::try_from(port).ok()?, udp_only)
    }

    fn new(host: &str, port: u16, udp_only: bool) -> Option<Self> {
        let host = host.trim_matches(['[', ']']);
        if host.is_empty() || port == 0 || host.chars().any(char::is_control) { return None; }
        Some(Self { host: host.to_owned(), port, udp_only })
    }

    pub(super) fn unsupported(&self, method: Method) -> Option<&'static str> {
        (method == Method::Tcp && self.udp_only).then_some("Профиль использует UDP. Для адреса выбери ICMP.")
    }

    pub(super) fn is_loopback(&self) -> bool {
        self.host.parse::<std::net::IpAddr>().is_ok_and(|address| address.is_loopback())
    }

    pub(super) fn probe_key(&self, method: Method) -> (&str, u16) {
        (self.host.as_str(), if method == Method::Tcp { self.port } else { 0 })
    }

    pub(super) fn measure(&self, method: Method, stop: &AtomicBool) -> Result<Option<i64>> {
        let addresses = match (self.host.as_str(), self.port).to_socket_addrs() {
            Ok(addresses) => addresses,
            Err(error) if socket_exhausted(&error) => return Err(error.into()),
            Err(_) => return Ok(None),
        };
        for addr in addresses.take(2) {
            if stop.load(Ordering::Acquire) { break; }
            match method {
                Method::Tcp => {
                    let start = Instant::now();
                    match TcpStream::connect_timeout(&addr, Duration::from_millis(1500)) {
                        Ok(_) => return Ok(Some((start.elapsed().as_millis() as i64).max(1))),
                        Err(error) if socket_exhausted(&error) => return Err(error.into()),
                        Err(_) => {}
                    }
                }
                Method::Icmp => {
                    if let Some(delay) = echo(addr)? { return Ok(Some(delay)); }
                }
                Method::Vpn => unreachable!("VPN checks use the local proxy"),
            }
        }
        Ok(None)
    }
}

fn echo(address: SocketAddr) -> Result<Option<i64>> {
    let packet = b"MORPH";
    let mut reply = [0u64; 32];
    let pointer = reply.as_mut_ptr().cast();
    let size = std::mem::size_of_val(&reply) as u32;
    // Both calls are synchronous. The aligned buffer holds a native echo reply and its payload.
    let delay = unsafe {
        match address {
            SocketAddr::V4(addr) => {
                let handle = IcmpCreateFile()?;
                let count = IcmpSendEcho(handle, u32::from_ne_bytes(addr.ip().octets()), packet.as_ptr().cast(),
                    packet.len() as u16, None, pointer, size, 1500);
                let error = (count == 0).then(std::io::Error::last_os_error);
                let _ = IcmpCloseHandle(handle);
                if let Some(error) = error.filter(|error| socket_exhausted(error)) { return Err(error.into()); }
                let response = &*pointer.cast::<ICMP_ECHO_REPLY>();
                (count != 0 && response.Status == IP_SUCCESS).then_some(response.RoundTripTime)
            }
            SocketAddr::V6(addr) => {
                let source = SOCKADDR_IN6 { sin6_family: AF_INET6, ..Default::default() };
                let mut destination = source;
                destination.sin6_addr.u.Byte = addr.ip().octets();
                destination.Anonymous.sin6_scope_id = addr.scope_id();
                let handle = Icmp6CreateFile()?;
                let count = Icmp6SendEcho2(handle, None, None, None, &source, &destination,
                    packet.as_ptr().cast(), packet.len() as u16, None, pointer, size, 1500);
                let error = (count == 0).then(std::io::Error::last_os_error);
                let _ = IcmpCloseHandle(handle);
                if let Some(error) = error.filter(|error| socket_exhausted(error)) { return Err(error.into()); }
                let response = &*pointer.cast::<ICMPV6_ECHO_REPLY_LH>();
                (count != 0 && response.Status == IP_SUCCESS).then_some(response.RoundTripTime)
            }
        }
    };
    Ok(delay.map(|ms| i64::from(ms.max(1))))
}
