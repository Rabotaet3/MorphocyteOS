from pathlib import Path
from hashlib import file_digest
import os
import re
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile


directory = Path(sys.argv[1]).resolve()
version = re.fullmatch(r"dist-(native-)?(\d+\.\d+\.\d+)", directory.name)
if version is None:
    sys.exit("Use a versioned build directory, such as dist-0.2.2.")

native = bool(version[1])
paths = [
    directory / "MorphocyteAtlas.exe",
    directory / "WebView2Loader.dll",
    *(directory / "backend" / name for name in ("release-source.json", "LICENSE.txt", "THIRD-PARTY-NOTICES.md")),
]
if not native:
    paths.extend(directory / "backend" / name for name in ("MorphocyteOS.exe", "portable.flag", "atlas-module.flag"))
for name in ("Licenses", "ThirdPartySource"):
    paths.extend(path for path in (directory / "backend" / name).rglob("*") if path.is_file())

archive = directory.parent / (f"MorphocyteOS-{version[2]}-portable-win-x64.zip" if native
                              else f"MorphocyteAtlas-{version[2]}-portable-win-x64.zip")
temp = archive.with_suffix(".tmp")
with ZipFile(temp, "w", compression=ZIP_DEFLATED, compresslevel=5) as package:
    for path in sorted(paths):
        package.write(path, (Path("MorphocyteAtlas") / path.relative_to(directory)).as_posix())
    package.writestr("MorphocyteAtlas/Запуск.txt",
        "Распакуй всю папку и открой MorphocyteAtlas.exe. Установка не нужна.\r\n"
        "Для окна требуется Microsoft Edge WebView2 Runtime.\r\n"
        "Профили и настройки хранятся в backend рядом с приложением.\r\n"
        + ("При запуске Windows запросит права администратора. При переносе на другой компьютер добавь подписку заново.\r\n" if native else
           "Для VPN Windows может запросить права администратора. Для переноса на другой компьютер можно экспортировать настройки.\r\n"))
temp.replace(archive)


def write_checksum(path):
    with path.open("rb") as binary:
        digest = file_digest(binary, "sha256").hexdigest()
    checksum = path.with_suffix(path.suffix + ".sha256")
    staged = checksum.with_suffix(".tmp")
    staged.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    staged.replace(checksum)
    print(path.name)
    print(checksum.name)


write_checksum(archive)
if native:
    rar = shutil.which("rar.exe") or Path(os.environ["ProgramFiles"]) / "WinRAR/Rar.exe"
    if not Path(rar).is_file():
        sys.exit("WinRAR is required to build the RAR release.")
    rar_archive = archive.with_suffix(".rar")
    with TemporaryDirectory(dir=directory.parent) as folder:
        with ZipFile(archive) as package:
            package.extractall(folder)
        staged = Path(folder) / rar_archive.name
        subprocess.run([rar, "a", "-ma5", "-m3", "-idq", staged, "MorphocyteAtlas"], check=True, cwd=folder)
        subprocess.run([rar, "t", "-idq", staged], check=True, cwd=folder)
        staged.replace(rar_archive)
    write_checksum(rar_archive)
