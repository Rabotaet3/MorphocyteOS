use super::{Result, SOCKET_RESOURCES, socket_exhausted};
use std::{
    io::{self, Read},
    sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}},
    thread,
    time::{Duration, Instant},
};

pub(super) const HOSTS: [&str; 2] = ["speed.cloudflare.com", "fsn1-speed.hetzner.com"];
const LIMIT: usize = 64_000_000;
const TIME: Duration = Duration::from_secs(24);
const SOURCE_TIME: Duration = Duration::from_secs(12);
const BIN_MILLIS: u128 = 100;
const BINS: usize = (SOURCE_TIME.as_millis() / BIN_MILLIS) as usize + 1;
const WINDOW_BINS: usize = 10;

#[derive(Clone, Copy)]
struct Source<'a> {
    url: &'a str,
    use_range: bool,
}

#[derive(Clone)]
pub(super) struct Channel {
    pub port: u16,
    pub password: String,
}

pub(super) struct Test {
    started: Instant,
    cancelled: Arc<AtomicBool>,
    result: Arc<Mutex<Measurement>>,
}

#[derive(Default)]
pub(super) struct Display {
    pub running: bool,
    pub speed: Option<String>,
    pub percent: Option<u8>,
    pub message: String,
}

struct Measurement {
    received: usize,
    sampled: usize,
    first: Option<Instant>,
    elapsed: Duration,
    bins: [usize; BINS],
    outcome: Option<Outcome>,
}

enum Outcome {
    Complete(f64),
    Cancelled,
    Failed(&'static str),
}

impl Measurement {
    fn new() -> Self {
        Self { received: 0, sampled: 0, first: None, elapsed: Duration::ZERO, bins: [0; BINS], outcome: None }
    }

    fn record(&mut self, bytes: usize, now: Instant) {
        self.received += bytes;
        let Some(first) = self.first else {
            self.first = Some(now);
            return;
        };
        self.elapsed = now - first;
        let index = (self.elapsed.as_millis() / BIN_MILLIS) as usize;
        if let Some(bin) = self.bins.get_mut(index) {
            *bin += bytes;
            self.sampled += bytes;
        }
    }

    fn peak(&self) -> Option<f64> {
        let seconds = self.elapsed.as_secs_f64();
        if self.sampled < 16_384 || seconds < 0.01 || (seconds < 0.25 && self.sampled < 4_000_000) {
            return None;
        }
        if seconds < 1.0 { return Some(self.sampled as f64 / seconds); }
        let complete = ((self.elapsed.as_millis() / BIN_MILLIS) as usize).min(self.bins.len());
        let peak = self.bins[..complete].windows(WINDOW_BINS).map(|window| window.iter().sum::<usize>())
            .max().unwrap_or(0);
        Some((peak as f64).max(self.sampled as f64 / seconds))
    }
}

impl Test {
    pub(super) fn start(channel: &Channel) -> Result<Self> {
        let proxy = ureq::Proxy::new(&format!("http://morphocyte:{}@127.0.0.1:{}", channel.password, channel.port))?;
        let agent = ureq::Agent::new_with_config(ureq::config::Config::builder()
            .proxy(Some(proxy)).max_redirects(0).timeout_global(Some(TIME))
            .timeout_connect(Some(Duration::from_secs(8))).timeout_recv_response(Some(Duration::from_secs(6)))
            .timeout_recv_body(Some(Duration::from_secs(8)))
            .tls_config(ureq::tls::TlsConfig::builder().provider(ureq::tls::TlsProvider::NativeTls)
                .root_certs(ureq::tls::RootCerts::PlatformVerifier).build())
            .build());
        let started = Instant::now();
        let cancelled = Arc::new(AtomicBool::new(false));
        let result = Arc::new(Mutex::new(Measurement::new()));
        let stop = Arc::clone(&cancelled);
        let data = Arc::clone(&result);
        thread::Builder::new().name("morphocyte-speed".into()).spawn(move || {
            let sources = [
                Source { url: &format!("https://{}/__down", HOSTS[0]), use_range: false },
                Source { url: &format!("https://{}/100MB.bin", HOSTS[1]), use_range: true },
            ];
            let outcome = measure(&agent, &sources, LIMIT, started, &stop, &data);
            data.lock().expect("speed measurement").outcome = Some(outcome);
        })?;
        Ok(Self { started, cancelled, result })
    }

    pub(super) fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
    }

    pub(super) fn running(&self) -> bool {
        self.result.lock().expect("speed measurement").outcome.is_none()
    }

    pub(super) fn display(&self) -> Display {
        let data = self.result.lock().expect("speed measurement");
        match &data.outcome {
            None => Display { running: true, speed: data.peak().map(format_speed),
                percent: Some(((data.received * 100 / LIMIT).max(
                    (self.started.elapsed().as_millis() * 100 / TIME.as_millis()) as usize)).min(99) as u8),
                message: if self.cancelled.load(Ordering::Acquire) { "Останавливаю замер…".into() } else { String::new() } },
            Some(Outcome::Complete(peak)) => Display { speed: Some(format_speed(*peak)), percent: Some(100), ..Default::default() },
            Some(Outcome::Cancelled) => Display { message: "Замер остановлен.".into(), ..Default::default() },
            Some(Outcome::Failed(message)) => Display { message: (*message).into(), ..Default::default() },
        }
    }
}

fn measure(agent: &ureq::Agent, sources: &[Source<'_>], limit: usize, started: Instant, stop: &AtomicBool,
    shared: &Mutex<Measurement>) -> Outcome {
    let mut failure = None;
    let deadline = started + TIME;
    for &source in sources {
        let now = Instant::now();
        if stop.load(Ordering::Acquire) || now >= deadline { break; }
        let source_deadline = (now + SOURCE_TIME).min(deadline);
        let received = shared.lock().expect("speed measurement").received;
        if received >= limit { break; }
        *shared.lock().expect("speed measurement") = Measurement { received, ..Measurement::new() };
        let remaining = limit - received;
        let half = remaining.div_ceil(2);
        let errors = thread::scope(|scope| {
            let first = scope.spawn(|| download(agent, source, received, half, source_deadline, stop, shared));
            let second = download(agent, source, received + half, remaining - half, source_deadline, stop, shared);
            [first.join().expect("speed worker"), second]
        });
        if stop.load(Ordering::Acquire) { return Outcome::Cancelled; }
        if let Some(rate) = shared.lock().expect("speed measurement").peak() { return Outcome::Complete(rate); }
        for error in errors.into_iter().filter_map(Result::err) {
            if socket_exhausted(error.as_ref()) { return Outcome::Failed(SOCKET_RESOURCES); }
            failure = Some(error);
        }
    }
    if stop.load(Ordering::Acquire) { return Outcome::Cancelled; }
    let received = shared.lock().expect("speed measurement").received;
    Outcome::Failed(failure_message(failure.as_deref(), received))
}

fn failure_message(error: Option<&(dyn std::error::Error + Send + Sync + 'static)>, received: usize) -> &'static str {
    if received > 0 { return "За время замера получено слишком мало данных. Повтори проверку."; }
    match error.and_then(|error| error.downcast_ref::<ureq::Error>()) {
        Some(ureq::Error::StatusCode(403 | 429)) => "Сервис замера ограничил запросы с этого VPN-сервера.",
        Some(ureq::Error::Timeout(ureq::Timeout::Connect)) => "Не удалось вовремя установить HTTPS-соединение с сервисами замера через VPN.",
        Some(ureq::Error::Timeout(_)) => "Сервисы замера не ответили вовремя через выбранный VPN-сервер.",
        Some(ureq::Error::NativeTls(_) | ureq::Error::Tls(_)) => "Не удалось установить защищённое соединение с сервисом замера.",
        _ if error.and_then(|error| error.downcast_ref::<io::Error>()).is_some_and(|error| error.kind() == io::ErrorKind::InvalidData) =>
            "Сервис замера вернул ответ без тестовых данных.",
        _ => "Сервисы замера недоступны через выбранный VPN-сервер.",
    }
}

fn download(agent: &ureq::Agent, source: Source<'_>, offset: usize, limit: usize, deadline: Instant, stop: &AtomicBool,
    shared: &Mutex<Measurement>) -> Result<()> {
    let time_left = deadline.saturating_duration_since(Instant::now());
    if limit == 0 || stop.load(Ordering::Acquire) || time_left.is_zero() { return Ok(()); }
    let url = if source.use_range { source.url.to_owned() } else { format!("{}?bytes={limit}", source.url) };
    let mut request = agent.get(url).header("Cache-Control", "no-cache, no-store").header("Accept-Encoding", "identity");
    let range = format!("{offset}-{}", offset + limit - 1);
    if source.use_range { request = request.header("Range", format!("bytes={range}")); }
    let mut response = request.config().timeout_global(Some(time_left)).build().call()?;
    let headers = response.headers();
    let content_type = headers.get("Content-Type").and_then(|value| value.to_str().ok())
        .and_then(|value| value.split(';').next().map(str::trim));
    let valid_range = !source.use_range || (response.status().as_u16() == 206 && headers.get("Content-Range")
        .and_then(|value| value.to_str().ok()).and_then(|value| value.strip_prefix("bytes "))
        .and_then(|value| value.split_once('/')).is_some_and(|(returned, _)| returned == range));
    if !response.status().is_success() || !valid_range || !content_type.is_some_and(|mime| mime.eq_ignore_ascii_case("application/octet-stream")) {
        return Err(io::Error::from(io::ErrorKind::InvalidData).into());
    }
    let mut reader = response.body_mut().with_config().limit(limit as u64).reader();
    let mut buffer = [0u8; 64 * 1024];
    let mut read = 0;
    while read < limit && !stop.load(Ordering::Acquire) && Instant::now() < deadline {
        let remaining = buffer.len().min(limit - read);
        let bytes = reader.read(&mut buffer[..remaining])?;
        if bytes == 0 { break; }
        read += bytes;
        shared.lock().expect("speed measurement").record(bytes, Instant::now());
    }
    if read == 0 && !stop.load(Ordering::Acquire) { return Err(io::Error::from(io::ErrorKind::UnexpectedEof).into()); }
    Ok(())
}

fn format_speed(bytes_per_second: f64) -> String {
    let (divisor, unit) = if bytes_per_second < 125_000.0 { (125.0, "кбит/с") }
        else if bytes_per_second < 125_000_000.0 { (125_000.0, "Мбит/с") } else { (125_000_000.0, "Гбит/с") };
    format!("{:.1} {unit}", bytes_per_second / divisor).replace('.', ",")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{io::{BufRead, BufReader, Write}, net::TcpListener};

    #[test]
    fn reports_progress_and_one_second_peak() {
        let now = Instant::now();
        let mut measurement = Measurement::new();
        measurement.record(64_000, now);
        for index in 1..=12 {
            measurement.record(1_000_000, now + Duration::from_millis(100 * index));
        }
        let test = Test { started: now, cancelled: Arc::new(AtomicBool::new(false)), result: Arc::new(Mutex::new(measurement)) };
        let live = test.display();
        assert!(live.running);
        assert_eq!(live.percent, Some(18));
        assert_eq!(live.speed.as_deref(), Some("80,0 Мбит/с"));
        let mut result = test.result.lock().unwrap();
        result.outcome = Some(Outcome::Complete(result.peak().unwrap()));
        drop(result);
        assert_eq!(test.display().percent, Some(100));
    }

    #[test]
    fn reports_a_slow_transfer_from_the_data_received() {
        let now = Instant::now();
        let mut measurement = Measurement::new();
        measurement.record(4096, now);
        measurement.record(32_768, now + Duration::from_secs(2));
        assert_eq!(measurement.peak(), Some(16_384.0));
    }

    #[test]
    fn uses_authenticated_connect_for_both_downloads() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let deadline = Instant::now() + Duration::from_secs(5);
            for host in HOSTS.into_iter().flat_map(|host| [host; 2]) {
                let stream = loop {
                    match listener.accept() {
                        Ok((stream, _)) => break stream,
                        Err(error) if error.kind() == io::ErrorKind::WouldBlock && Instant::now() < deadline => {
                            thread::sleep(Duration::from_millis(10));
                        }
                        Err(error) => panic!("No speed connection to local proxy: {error}"),
                    }
                };
                stream.set_nonblocking(false).unwrap();
                stream.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
                let mut reader = BufReader::new(stream);
                let mut line = String::new();
                reader.read_line(&mut line).unwrap();
                assert!(line.starts_with(&format!("CONNECT {host}:443 HTTP/1.1")), "{line}");
                let mut authenticated = false;
                loop {
                    line.clear();
                    assert!(reader.read_line(&mut line).unwrap() > 0);
                    if line == "\r\n" { break; }
                    authenticated |= line.to_ascii_lowercase().starts_with("proxy-authorization: basic ");
                }
                assert!(authenticated);
                reader.get_mut().write_all(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n").unwrap();
            }
        });
        let test = Test::start(&Channel { port, password: "test-password".to_owned() }).unwrap();
        server.join().unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while test.running() && Instant::now() < deadline { thread::sleep(Duration::from_millis(10)); }
        let failed = test.display();
        assert!(!failed.running);
        assert!(failed.speed.is_none() && failed.percent.is_none());
        assert!(!failed.message.is_empty());
    }

    #[test]
    fn counts_downloaded_bytes_up_to_the_budget() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let buffer = [9u8; 64 * 1024];
        let budget = 8 * buffer.len();
        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
            let mut stream = BufReader::new(stream);
            let mut line = String::new();
            loop {
                line.clear();
                assert!(stream.read_line(&mut line).unwrap() > 0);
                if line == "\r\n" { break; }
            }
            let stream = stream.get_mut();
            write!(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {}\r\n\r\n",
                budget + buffer.len()).unwrap();
            for _ in 0..8 {
                stream.write_all(&buffer).unwrap();
                thread::sleep(Duration::from_millis(45));
            }
        });
        let shared = Mutex::new(Measurement::new());
        let url = format!("http://127.0.0.1:{port}/");
        download(&super::super::http_agent(Duration::from_secs(3)), Source { url: &url, use_range: false },
            0, budget, Instant::now() + Duration::from_secs(3), &AtomicBool::new(false), &shared).unwrap();
        server.join().unwrap();
        let result = shared.lock().unwrap();
        assert_eq!(result.received, budget);
        assert!(result.peak().is_some());
    }

    #[test]
    fn falls_back_to_ranged_downloads_within_the_same_budget() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || thread::scope(|scope| {
            let deadline = Instant::now() + Duration::from_secs(5);
            let mut handlers = Vec::new();
            for _ in 0..4 {
                let stream = loop {
                    match listener.accept() {
                        Ok((stream, _)) => break stream,
                        Err(error) if error.kind() == io::ErrorKind::WouldBlock && Instant::now() < deadline => {
                            thread::sleep(Duration::from_millis(10));
                        }
                        Err(error) => panic!("Missing speed test request: {error}"),
                    }
                };
                handlers.push(scope.spawn(move || {
                    stream.set_nonblocking(false).unwrap();
                    stream.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
                    let mut reader = BufReader::new(stream);
                    let mut line = String::new();
                    reader.read_line(&mut line).unwrap();
                    let primary = line.starts_with("GET /primary?bytes=262144 ");
                    assert!(primary || line.starts_with("GET /backup "), "{line}");
                    let mut range = String::new();
                    loop {
                        line.clear();
                        assert!(reader.read_line(&mut line).unwrap() > 0);
                        if line == "\r\n" { break; }
                        if let Some(value) = line.to_ascii_lowercase().strip_prefix("range: bytes=") {
                            range = value.trim().to_owned();
                        }
                    }
                    let stream = reader.get_mut();
                    if primary {
                        stream.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
                        return None;
                    }
                    assert!(matches!(range.as_str(), "0-262143" | "262144-524287"), "{range}");
                    write!(stream, "HTTP/1.1 206 Partial Content\r\nContent-Type: application/octet-stream\r\nContent-Range: bytes {range}/104857600\r\nContent-Length: 262144\r\nConnection: close\r\n\r\n").unwrap();
                    for _ in 0..4 {
                        stream.write_all(&[9u8; 64 * 1024]).unwrap();
                        thread::sleep(Duration::from_millis(110));
                    }
                    Some(range)
                }));
            }
            let mut ranges = handlers.into_iter().filter_map(|handler| handler.join().unwrap()).collect::<Vec<_>>();
            ranges.sort();
            assert_eq!(ranges, ["0-262143", "262144-524287"]);
        }));
        let shared = Mutex::new(Measurement::new());
        let sources = [Source { url: &format!("http://127.0.0.1:{port}/primary"), use_range: false },
            Source { url: &format!("http://127.0.0.1:{port}/backup"), use_range: true }];
        let outcome = measure(&super::super::http_agent(Duration::from_secs(3)), &sources, 524_288,
            Instant::now(), &AtomicBool::new(false), &shared);
        server.join().unwrap();
        assert!(matches!(outcome, Outcome::Complete(rate) if rate > 0.0));
        assert_eq!(shared.lock().unwrap().received, 524_288);
    }
}
