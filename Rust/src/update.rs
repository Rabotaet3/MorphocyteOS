use std::{
    fs::{self, OpenOptions},
    io,
    os::windows::process::CommandExt,
    path::PathBuf,
    process::{Child, Command},
    time::{Duration, Instant},
};

fn cache() -> io::Result<PathBuf> {
    #[cfg(feature = "native-router")]
    return Ok(morphocyte_atlas::native::root()?.join(".atlas-updates"));
    #[cfg(not(feature = "native-router"))]
    {
        let mut path = crate::bridge::path()?;
        path.set_file_name(".atlas-updates");
        Ok(path)
    }
}

pub fn installing() -> bool {
    let Ok(cache) = cache() else { return false };
    let result = OpenOptions::new().read(true).open(cache.join("install.lock"));
    result.is_err_and(|error| error.raw_os_error() == Some(32))
}

pub struct Prepared {
    directory: PathBuf,
    child: Child,
}

impl Prepared {
    pub fn commit(self) -> io::Result<()> {
        fs::File::create_new(self.directory.join("commit"))?.sync_all()
    }

    pub fn abort(mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

pub fn start(id: &str, resume: bool) -> io::Result<Prepared> {
    if id.len() != 32 || !id.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(io::Error::other("Неверный идентификатор обновления"));
    }
    let operation = cache()?.join(id);
    #[cfg(feature = "native-router")]
    let mut command = {
        let windows = std::env::var_os("SystemRoot").ok_or_else(|| io::Error::other("Папка Windows недоступна"))?;
        let mut command = Command::new(PathBuf::from(windows).join("System32/WindowsPowerShell/v1.0/powershell.exe"));
        command.args(["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File"])
            .arg(operation.join("updater.ps1")).args(["-Mode","Apply","-ShellPid"])
            .arg(std::process::id().to_string()).arg("-Resume");
        command
    };
    #[cfg(not(feature = "native-router"))]
    let mut command = {
        let mut command = Command::new(operation.join("updater.exe"));
        command.arg("--atlas-apply-update").arg(std::process::id().to_string());
        command
    };
    let mut child = command.arg(if resume { "resume" } else { "idle" })
        .creation_flags(0x0800_0000)
        .spawn()?;
    let ready = operation.join("ready");
    let deadline = Instant::now() + Duration::from_secs(12);
    while Instant::now() < deadline {
        if child.try_wait()?.is_some() { return Err(io::Error::other("Установщик завершился до запуска")); }
        if ready.is_file() { return Ok(Prepared { directory: operation, child }); }
        std::thread::sleep(Duration::from_millis(50));
    }
    let _ = child.kill();
    let _ = child.wait();
    Err(io::Error::other("Установщик не успел запуститься"))
}
