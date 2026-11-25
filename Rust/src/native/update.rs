use super::{Result, error_message, http_agent, problem, random_token};
use serde_json::{Value, json};
use std::{
    fs::{self, File}, io::{self, Read}, os::windows::process::CommandExt,
    path::{Path, PathBuf}, process::Command, sync::mpsc::{self, Receiver, Sender},
    thread, time::Duration,
};
use url::Url;

const REPOSITORY: &str = "Rabotaet3/MorphocyteOS";
const CACHE: &str = ".atlas-updates";
const MAX_ARCHIVE: u64 = 512 * 1024 * 1024;
const AGENT: &str = concat!("MorphocyteOS/", env!("CARGO_PKG_VERSION"));

#[derive(Clone)]
struct Release {
    version: String,
    url: Url,
    digest: String,
    size: u64,
}

#[derive(Default)]
pub(super) struct Update {
    release: Option<Release>,
    task: Option<Receiver<Event>>,
    checking: bool,
    progress: Option<Value>,
    ready: Option<String>,
    message: String,
}

enum Event {
    Checked(Result<Option<Release>>),
    Progress(Value),
    Prepared(Result<String>),
}

impl Update {
    pub(super) fn load(root: &Path) -> Self {
        let cache = root.join(CACHE);
        let mut update = Self::default();
        if let Ok(result) = fs::read_to_string(cache.join("result.json"))
            && let Ok(result) = serde_json::from_str::<Value>(result.trim_start_matches('\u{feff}'))
        {
            update.message = result["message"].as_str().unwrap_or_default().to_owned();
            let _ = fs::remove_file(cache.join("result.json"));
            if result["cleanup"] == true
                && let Some(id) = result["operation"].as_str().filter(|id| valid_id(id))
            {
                let folder = cache.join(id);
                thread::spawn(move || { let _ = fs::remove_dir_all(folder); });
            }
        }
        update
    }

    pub(super) fn check(&mut self) {
        if self.task.is_some() || self.ready.is_some() { return; }
        self.checking = true;
        self.message = "Проверяю обновления…".into();
        let (sender, receiver) = mpsc::channel();
        self.task = Some(receiver);
        thread::spawn(move || { let _ = sender.send(Event::Checked(check_release())); });
    }

    pub(super) fn install(&mut self, root: &Path, version: &str) -> Result<()> {
        if self.task.is_some() || self.ready.is_some() { return Err(problem("Дождись завершения обновления.")); }
        let release = self.release.clone().filter(|release| release.version == version)
            .ok_or_else(|| problem("Проверь обновления ещё раз."))?;
        let (sender, receiver) = mpsc::channel();
        self.task = Some(receiver);
        self.progress = Some(json!({"fraction":0,"message":"Загружаю архив с GitHub…"}));
        let root = root.to_owned();
        thread::spawn(move || {
            let result = prepare(&root, &release, &sender);
            let _ = sender.send(Event::Prepared(result));
        });
        Ok(())
    }

    pub(super) fn collect(&mut self) {
        while let Some(event) = self.task.as_ref().and_then(|task| task.try_recv().ok()) {
            match event {
                Event::Checked(result) => {
                    self.task = None;
                    self.checking = false;
                    match result {
                        Ok(release) => {
                            self.message = if release.is_some() { "Доступно обновление" } else { "Установлена актуальная сборка" }.into();
                            self.release = release;
                        }
                        Err(error) => self.message = format!("Не удалось проверить обновления. {}", error_message(error.as_ref())),
                    }
                }
                Event::Progress(value) => self.progress = Some(value),
                Event::Prepared(result) => {
                    self.task = None;
                    match result {
                        Ok(id) => {
                            self.ready = Some(id);
                            self.progress = Some(json!({"fraction":1,"message":"Устанавливаю обновление…"}));
                        }
                        Err(error) => self.progress = Some(json!({"message":error_message(error.as_ref())})),
                    }
                }
            }
        }
    }

    pub(super) fn failed(&mut self) {
        self.ready = None;
        self.progress = Some(json!({"message":"Не удалось запустить установку. Повтори попытку."}));
    }

    pub(super) fn version(&self) -> Option<&str> { self.release.as_ref().map(|release| release.version.as_str()) }
    pub(super) fn message(&self) -> &str { &self.message }
    pub(super) fn checking(&self) -> bool { self.checking }
    pub(super) fn ready(&self) -> Option<&str> { self.ready.as_deref() }
    pub(super) fn progress(&self) -> Option<&Value> { self.progress.as_ref() }
    pub(super) fn installing(&self) -> bool { self.ready.is_some() || (!self.checking && self.task.is_some()) }
}

fn version_tuple(text: &str) -> Option<[u32; 3]> {
    let mut parts = text.split('.');
    let result = [parts.next()?.parse().ok()?, parts.next()?.parse().ok()?, parts.next()?.parse().ok()?];
    parts.next().is_none().then_some(result)
}

fn valid_id(id: &str) -> bool { id.len() == 32 && id.bytes().all(|byte| byte.is_ascii_hexdigit()) }

fn check_release() -> Result<Option<Release>> {
    let endpoint = format!("https://api.github.com/repos/{REPOSITORY}/releases/latest");
    let mut response = http_agent(Duration::from_secs(12)).get(endpoint)
        .header("User-Agent", AGENT).header("Accept", "application/vnd.github+json").call()?;
    let data: Value = response.body_mut().with_config().limit(2 * 1024 * 1024).read_json()?;
    release_asset(&data)
}

fn release_asset(data: &Value) -> Result<Option<Release>> {
    let Some(tag) = data["tag_name"].as_str().filter(|_| data["draft"] == false && data["prerelease"] == false) else { return Ok(None); };
    let version = tag.strip_prefix('v').unwrap_or(tag);
    let Some(number) = version_tuple(version) else { return Ok(None); };
    if number <= version_tuple(env!("CARGO_PKG_VERSION")).expect("stable version") { return Ok(None); }
    let name = format!("MorphocyteOS-{version}-portable-win-x64.zip");
    let Some(asset) = data["assets"].as_array().and_then(|assets| assets.iter().find(|asset| asset["name"] == name)) else { return Ok(None); };
    let address = format!("https://github.com/{REPOSITORY}/releases/download/{tag}/{name}");
    if asset["browser_download_url"] != address || asset["state"] != "uploaded" {
        return Err(problem("GitHub вернул неверный адрес архива обновления."));
    }
    let digest = asset["digest"].as_str().and_then(|value| value.strip_prefix("sha256:"))
        .filter(|digest| digest.len() == 64 && digest.bytes().all(|byte| byte.is_ascii_hexdigit()))
        .ok_or_else(|| problem("У архива обновления нет контрольной суммы SHA-256."))?;
    let size = asset["size"].as_u64().filter(|size| (1..=MAX_ARCHIVE).contains(size))
        .ok_or_else(|| problem("Недопустимый размер архива обновления."))?;
    Ok(Some(Release { version:version.into(), url:Url::parse(&address)?, digest:digest.into(), size }))
}

fn prepare(root: &Path, release: &Release, sender: &Sender<Event>) -> Result<String> {
    let id = random_token()?[..32].to_owned();
    let operation = root.join(CACHE).join(&id);
    for path in operation.ancestors().skip(1) {
        if redirects_path(path)? { return Err(problem("Папка обновления находится по ссылке.")); }
    }
    fs::create_dir_all(operation.parent().expect("update cache"))?;
    fs::create_dir(&operation)?;
    let result = (|| {
        let archive = operation.join("package.zip");
        download(release, &archive, sender)?;
        let _ = sender.send(Event::Progress(json!({"fraction":0.92,"message":"Проверяю и распаковываю архив…"})));
        let script = operation.join("updater.ps1");
        fs::write(&script, [b"\xef\xbb\xbf".as_slice(), include_bytes!("update.ps1")].concat())?;
        let outcome = Command::new(powershell()?).args(["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File"])
            .arg(&script).args(["-Mode","Stage","-Digest",&release.digest])
            .creation_flags(0x0800_0000).output()?;
        if !outcome.status.success() { return Err(problem("Не удалось проверить или распаковать ZIP обновления.")); }
        fs::remove_file(&archive)?;
        Ok(id)
    })();
    if result.is_err() { let _ = fs::remove_dir_all(&operation); }
    result
}

fn redirects_path(path: &Path) -> Result<bool> {
    use std::os::windows::{ffi::OsStrExt, fs::MetadataExt};
    use windows::{Win32::Storage::FileSystem::{FindClose, FindFirstFileW, WIN32_FIND_DATAW}, core::PCWSTR};
    let metadata = match fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error.into()),
    };
    if metadata.file_attributes() & 0x400 == 0 { return Ok(false); }
    let wide = path.as_os_str().encode_wide().chain(Some(0)).collect::<Vec<_>>();
    let mut data = WIN32_FIND_DATAW::default();
    unsafe {
        let handle = FindFirstFileW(PCWSTR(wide.as_ptr()), &mut data)?;
        FindClose(handle)?;
    }
    // Cloud Files tags include the placeholder directories used by OneDrive.
    Ok(data.dwReserved0 & 0xffff_0fff != 0x9000_001a)
}

fn powershell() -> io::Result<PathBuf> {
    let windows = std::env::var_os("SystemRoot").ok_or_else(|| io::Error::other("Папка Windows недоступна."))?;
    Ok(PathBuf::from(windows).join("System32/WindowsPowerShell/v1.0/powershell.exe"))
}

fn download(release: &Release, target: &Path, sender: &Sender<Event>) -> Result<()> {
    let agent = http_agent(Duration::from_secs(600));
    let mut url = release.url.clone();
    for _ in 0..6 {
        if url.scheme() != "https" || url.port().is_some() || !url.username().is_empty() || url.password().is_some()
            || !matches!(url.host_str(), Some("github.com" | "release-assets.githubusercontent.com" | "objects.githubusercontent.com"))
        { return Err(problem("GitHub перенаправил загрузку на недоверенный адрес.")); }
        let mut response = agent.get(url.as_str()).header("User-Agent",AGENT).call()?;
        if response.status().is_redirection() {
            url = url.join(response.headers().get("location").and_then(|value| value.to_str().ok())
                .ok_or_else(|| problem("GitHub не вернул адрес файла."))?)?;
            continue;
        }
        if response.status() != 200 { return Err(problem("GitHub не отдал архив обновления.")); }
        let mut file = File::create_new(target)?;
        let mut input = response.body_mut().with_config().limit(release.size + 1).reader();
        let mut received = 0;
        let mut next_report = 0;
        while received <= release.size {
            let count = io::copy(&mut input.by_ref().take(256 * 1024), &mut file)?;
            received += count;
            if count == 0 { break; }
            if received >= next_report {
                next_report = received + 1024 * 1024;
                let _ = sender.send(Event::Progress(json!({
                    "fraction": 0.9 * received as f64 / release.size as f64,
                    "message": format!("Загружено {:.1} из {:.1} МБ", received as f64 / 1048576., release.size as f64 / 1048576.)
                })));
            }
        }
        if received != release.size { return Err(problem("Размер ZIP не совпадает с данными GitHub.")); }
        return Ok(());
    }
    Err(problem("Слишком много перенаправлений GitHub."))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn release() -> Value {
        json!({"tag_name":"v9999.0.0","draft":false,"prerelease":false,"assets":[{
            "name":"MorphocyteOS-9999.0.0-portable-win-x64.zip", "size":123, "state":"uploaded",
            "browser_download_url":format!("https://github.com/{REPOSITORY}/releases/download/v9999.0.0/MorphocyteOS-9999.0.0-portable-win-x64.zip"),
            "digest":format!("sha256:{}", "a".repeat(64))
        }]})
    }

    #[test]
    fn accepts_only_a_new_stable_zip_with_github_digest() {
        let data = release();
        assert_eq!(release_asset(&data).unwrap().unwrap().version, "9999.0.0");
        let mut invalid = data;
        invalid["assets"][0]["digest"] = Value::Null;
        assert!(release_asset(&invalid).is_err());
        invalid["prerelease"] = json!(true);
        assert!(release_asset(&invalid).unwrap().is_none());
        invalid["prerelease"] = json!(false);
        invalid["tag_name"] = json!(format!("v{}", env!("CARGO_PKG_VERSION")));
        assert!(release_asset(&invalid).unwrap().is_none());
    }

    #[test]
    fn rejects_download_urls_outside_the_configured_release() {
        let mut data = release();
        data["assets"][0]["browser_download_url"] = json!("https://example.invalid/file.zip");
        assert!(release_asset(&data).is_err());
    }
}
