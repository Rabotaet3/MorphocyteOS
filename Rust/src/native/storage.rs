use super::{Result, http_agent, problem, random_token, socket_exhausted};
use super::profile::{MAX_PROFILE, Profile};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::{fs, io::{ErrorKind, Write}, path::{Path, PathBuf}, time::Duration};
use windows::{
    Win32::{Foundation::{HLOCAL, HWND, LocalFree}, Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    }, System::Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RRF_SUBKEY_WOW6464KEY, RegGetValueW},
    UI::Controls::Dialogs::{CommDlgExtendedError, GetOpenFileNameW, GetSaveFileNameW, OFN_FILEMUSTEXIST,
        OFN_NOCHANGEDIR, OFN_OVERWRITEPROMPT, OFN_PATHMUSTEXIST, OPENFILENAMEW}},
    core::{PCWSTR, PWSTR, w},
};

const SUBSCRIPTION: &[u8] = b"MorphocyteOS.Subscription.1";
const DEVICE: &[u8] = b"MorphocyteOS.SubscriptionDevice.1";

pub(crate) fn load(root: &Path) -> Value {
    for name in ["native-settings.json", "settings.json", "settings.json.bak"] {
        if let Ok(raw) = fs::read_to_string(root.join(name))
            && let Ok(value) = serde_json::from_str::<Value>(raw.trim_start_matches('\u{feff}'))
            && value.is_object()
        {
            return value;
        }
    }
    json!({})
}

pub(crate) fn save_file(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().ok_or_else(|| problem("Некорректная папка данных."))?;
    fs::create_dir_all(parent)?;
    let temporary = parent.join(format!("{}.tmp", random_token()?));
    let result = (|| -> Result<()> {
        let mut file = fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
        file.write_all(content)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)?;
        Ok(())
    })();
    if result.is_err() { let _ = fs::remove_file(temporary); }
    result
}

pub(crate) fn resolve(root: &Path, name: &str) -> PathBuf {
    root.join(name)
}

fn crypt(data: &[u8], entropy: &[u8], protect: bool) -> Result<Vec<u8>> {
    let input = CRYPT_INTEGER_BLOB { cbData: data.len().try_into()?, pbData: data.as_ptr().cast_mut() };
    let entropy = CRYPT_INTEGER_BLOB { cbData: entropy.len().try_into()?, pbData: entropy.as_ptr().cast_mut() };
    let mut output = CRYPT_INTEGER_BLOB::default();
    unsafe {
        if protect {
            CryptProtectData(&input, PCWSTR::null(), Some(&entropy), None, None,
                CRYPTPROTECT_UI_FORBIDDEN, &mut output)?;
        } else {
            CryptUnprotectData(&input, None, Some(&entropy), None, None,
                CRYPTPROTECT_UI_FORBIDDEN, &mut output)?;
        }
        let response = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(Some(HLOCAL(output.pbData.cast())));
        Ok(response)
    }
}

pub(crate) fn protect_url(url: &str) -> Result<String> {
    Ok(STANDARD.encode(crypt(url.as_bytes(), SUBSCRIPTION, true)?))
}

pub(crate) fn read_url(source: &str) -> Result<String> {
    if source.len() > 65536 { return Err(problem("Сохранённая ссылка недоступна.")); }
    Ok(String::from_utf8(crypt(&STANDARD.decode(source)?, SUBSCRIPTION, false)?)?)
}

fn valid_guid(value: &str) -> bool {
    value.len() == 36 && value.bytes().enumerate().all(|(index, un)| {
        if matches!(index, 8 | 13 | 18 | 23) { un == b'-' } else { un.is_ascii_hexdigit() }
    }) && value != "00000000-0000-0000-0000-000000000000"
}

fn computer_guid() -> Option<String> {
    let mut buffer = [0u16; 128];
    let mut length = (buffer.len() * 2) as u32;
    let status = unsafe { RegGetValueW(HKEY_LOCAL_MACHINE, w!("SOFTWARE\\Microsoft\\Cryptography"), w!("MachineGuid"),
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, None, Some(buffer.as_mut_ptr().cast()), Some(&mut length)) };
    if status.0 != 0 { return None; }
    let value = String::from_utf16(&buffer[..buffer.iter().position(|unit| *unit == 0)?]).ok()?;
    valid_guid(&value).then_some(value)
}

fn device(root: &Path) -> Result<String> {
    let path = root.join("subscription-device-id.dat");
    let existing = match fs::read(&path) {
        Ok(data) => Some(data),
        Err(error) if error.kind() == ErrorKind::NotFound => None,
        Err(error) => return Err(error.into()),
    };
    if let Some(data) = existing {
        if data.len() > 4096 { return Err(problem("Сохранённый ID устройства повреждён.")); }
        let id = String::from_utf8(crypt(&data, DEVICE, false)?)?;
        return if valid_guid(&id) { Ok(id) } else { Err(problem("Сохранённый ID устройства повреждён.")) };
    }
    let id = match computer_guid() {
        Some(id) => id,
        None => {
            let hex = random_token()?;
            format!("{}-{}-{}-{}-{}", &hex[..8], &hex[8..12], &hex[12..16], &hex[16..20], &hex[20..32])
        }
    };
    fs::create_dir_all(root)?;
    let mut file = fs::OpenOptions::new().create_new(true).write(true).open(path)?;
    file.write_all(&crypt(id.as_bytes(), DEVICE, true)?)?;
    file.sync_all()?;
    Ok(id)
}

pub(crate) fn download(root: &Path, source: &str) -> Result<(Profile, Value)> {
    let origin = url::Url::parse(source.trim()).map_err(|_| problem("Некорректный адрес подписки."))?;
    let allowed = |url: &url::Url| url.scheme() == "https" && url.host_str().is_some()
        && url.username().is_empty() && url.password().is_none() && url.origin() == origin.origin();
    if !allowed(&origin) { return Err(problem("Для подписки нужна ссылка HTTPS.")); }
    let id = device(root)?;
    let agent = http_agent(Duration::from_secs(18));
    let mut uri = origin.clone();
    for _ in 0..=5 {
        let response = agent.get(uri.as_str()).header("User-Agent", "Happ/compatible (Windows) MorphocyteOS/Rust")
            .header("x-device-os", "Windows").header("x-hwid", &id)
            .header("Accept", "application/yaml, application/json, text/plain, */*").call()
            .map_err(|error| {
                if socket_exhausted(&error) { return error.into(); }
                problem("Не удалось загрузить подписку. Проверь адрес, доступность и сертификат.")
            })?;
        if response.status().is_redirection() {
            let location = response.headers().get("location").and_then(|value| value.to_str().ok())
                .ok_or_else(|| problem("Сервер вернул некорректное перенаправление."))?;
            uri = uri.join(location).map_err(|_| problem("Некорректное перенаправление подписки."))?;
            if !allowed(&uri) { return Err(problem("Вставь конечную HTTPS-ссылку провайдера.")); }
            continue;
        }
        if !response.status().is_success() { return Err(problem("Сервер отклонил запрос подписки.")); }
        let (parts, mut body) = response.into_parts();
        let input = body.with_config().limit(MAX_PROFILE as u64).read_to_string()
            .map_err(|_| problem("Ответ подписки слишком большой или не читается."))?;
        let profile = Profile::parse(&input)?;
        let title = parts.headers.get("profile-title").and_then(|value| value.to_str().ok())
            .and_then(|value| match value.strip_prefix("base64:") {
                Some(value) => STANDARD.decode(value).ok().and_then(|bytes| String::from_utf8(bytes).ok()),
                None => Some(value.to_owned()),
            }).unwrap_or_else(|| "Моя подписка".to_owned());
        let announcement = parts.headers.get("announce").and_then(|value| value.to_str().ok()).unwrap_or_default();
        return Ok((profile, json!({"title":title,"announcement":announcement})));
    }
    Err(problem("Сервер вернул слишком много перенаправлений."))
}

pub(crate) fn pick_file(window: isize, title: &str, save: bool) -> Result<Option<PathBuf>> {
    let mut file = vec![0u16; 32768];
    let title = title.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
    let filters = "Профили и настройки\0*.yaml;*.yml;*.json;*.txt\0Все файлы\0*.*\0\0".encode_utf16().collect::<Vec<_>>();
    let mut request = OPENFILENAMEW {
        lStructSize: std::mem::size_of::<OPENFILENAMEW>() as u32,
        hwndOwner: HWND(window as _),
        lpstrFile: PWSTR(file.as_mut_ptr()),
        nMaxFile: file.len() as u32,
        lpstrFilter: PCWSTR(filters.as_ptr()),
        lpstrTitle: PCWSTR(title.as_ptr()),
        Flags: OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | if save { OFN_OVERWRITEPROMPT } else { OFN_FILEMUSTEXIST },
        ..Default::default()
    };
    let selected = if save { unsafe { GetSaveFileNameW(&mut request) } } else { unsafe { GetOpenFileNameW(&mut request) } };
    if !selected.as_bool() {
        let error = unsafe { CommDlgExtendedError() };
        return if error.0 == 0 { Ok(None) } else { Err(problem(format!("Windows не открыла выбор файла (код {}).", error.0))) };
    }
    Ok(Some(PathBuf::from(String::from_utf16(&file[..file.iter().position(|ch| *ch == 0).unwrap_or(file.len())])?)))
}
