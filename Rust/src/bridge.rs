use serde_json::{Value, json};
use std::io::{BufRead, BufReader, Read, Write};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, SyncSender, TrySendError};

const MAX_RESPONSE: usize = 4 * 1024 * 1024;

pub fn path() -> std::io::Result<PathBuf> {
    let executable = std::env::current_exe()?;
    let directory = executable.parent().ok_or_else(|| {
        std::io::Error::other("Не удалось определить папку приложения")
    })?;
    Ok(directory.join("backend").join("MorphocyteOS.exe"))
}

fn spawn(backend: &PathBuf, window: isize) -> std::io::Result<Child> {
    let mut command = Command::new(backend);
    command
        .arg("--atlas-desktop")
        .env("MORPHOCYTE_ATLAS_HWND", window.to_string())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    if std::env::args().any(|arg| arg == "--startup") {
        command.arg("--startup");
    } else if std::env::args().any(|arg| arg == "--connect-admin") {
        command.arg("--connect-admin");
    }
    #[cfg(windows)]
    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    if let Some(directory) = backend.parent() {
        command.current_dir(directory);
    }
    command.spawn()
}

pub struct Backend {
    sender: SyncSender<Value>,
}

impl Backend {
    pub fn start(window: isize, reply: impl Fn(Value) + Send + 'static) -> Self {
        let (sender, receiver) = mpsc::sync_channel::<Value>(6);
        std::thread::spawn(move || {
            let mut child = match path().and_then(|location| spawn(&location, window)) {
                Ok(child) => child,
                Err(error) => {
                    let message = format!("Не удалось запустить модуль Морфоцита 1.3.6: {error}");
                    for request in receiver { reply(disconnected(&request, &message)); }
                    return;
                }
            };
            #[cfg(windows)]
            let _lifetime = match crate::desktop::attach_backend(&child) {
                Ok(job) => job,
                Err(error) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    let message = format!("Не удалось привязать модуль VPN к окну Морфоцита: {error}");
                    for request in receiver { reply(disconnected(&request, &message)); }
                    return;
                }
            };
            let Some(mut input) = child.stdin.take() else {
                return;
            };
            let Some(output) = child.stdout.take() else {
                return;
            };
            let mut output = BufReader::new(output);
            for request in receiver {
                match exchange(&mut input, &mut output, &request) {
                    Ok(response) if request["op"] == "shutdown" && response["ok"] == true => {
                        drop(input);
                        let _ = child.wait();
                        reply(response);
                        return;
                    }
                    Ok(response) => reply(response),
                    Err(error) => {
                        drop(input);
                        let code = child.try_wait().ok().flatten().and_then(|status| status.code());
                        let message = match code {
                            Some(31) => "Не найден файл portable.flag в папке backend.".to_owned(),
                            Some(32) => "Эта копия Морфоцита уже открыта. Переключись на её окно.".to_owned(),
                            Some(code) => format!("Модуль Морфоцита завершился (код {code}). {error}"),
                            None => error.to_string(),
                        };
                        reply(disconnected(&request, &message));
                        return;
                    }
                }
            }
            drop(input);
            let _ = child.wait();
        });
        Self { sender }
    }

    pub fn send(&self, request: Value) -> Option<Value> {
        match self.sender.try_send(request) {
            Ok(()) => None,
            Err(TrySendError::Full(request)) => {
                Some(failure(&request, "Предыдущая операция ещё выполняется"))
            }
            Err(TrySendError::Disconnected(request)) => {
                Some(disconnected(&request, "Связь с модулем Морфоцита прервана"))
            }
        }
    }

    pub fn shutdown(&self, already_stopped: impl FnOnce() + Send + 'static) {
        let sender = self.sender.clone();
        std::thread::spawn(move || {
            if sender.send(json!({"id":0, "op":"shutdown"})).is_err() {
                already_stopped();
            }
        });
    }
}

fn exchange(
    input: &mut impl Write,
    output: &mut impl BufRead,
    request: &Value,
) -> std::io::Result<Value> {
    serde_json::to_writer(&mut *input, request)?;
    input.write_all(b"\n")?;
    input.flush()?;
    let mut line = Vec::new();
    let bytes = output.take(MAX_RESPONSE as u64).read_until(b'\n', &mut line)?;
    if bytes == 0 || line.last() != Some(&b'\n') {
        return Err(std::io::Error::other("Модуль Морфоцита не вернул полный ответ"));
    }
    let reply: Value = serde_json::from_slice(&line)?;
    if reply.get("id") != request.get("id") {
        return Err(std::io::Error::other("Ответ получен для другой операции"));
    }
    Ok(reply)
}

fn failure(request: &Value, message: &str) -> Value {
    json!({ "id": request.get("id"), "ok": false, "error": message })
}

fn disconnected(request: &Value, message: &str) -> Value {
    json!({ "id": request.get("id"), "ok": false, "error": message, "disconnected": true })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn exchange_preserves_a_cyrillic_profile_name() {
        let mut written = Vec::new();
        let request = json!({"id": 4, "op": "select", "name": "Финляндия"});
        let mut input = Cursor::new(b"{\"id\":4,\"ok\":true}\n");
        let reply = exchange(&mut written, &mut input, &request).unwrap();
        assert_eq!(serde_json::from_slice::<Value>(&written).unwrap(), request);
        assert_eq!(reply["ok"], true);
    }

    #[test]
    fn lost_pipe_and_mismatched_responses_are_errors() {
        let request = json!({"id": 4});
        for reply in [&b""[..], &b"{\"id\":4}"[..], &b"{\"id\":2}\n"[..]] {
            assert!(exchange(&mut Vec::new(), &mut Cursor::new(reply), &request).is_err());
        }
    }
}
