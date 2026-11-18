# Исходники Mihomo

## Mihomo 1.19.34

Поставляется неизменённый официальный Windows amd64 бинарник mihomo.exe из mihomo-windows-amd64-v1-v1.19.34.zip. Он работает отдельным процессом. Версия Go официального бинарника — 1.26.11.

- Upstream commit — 0455d0d28c4526937380505153e65a843d8612e8.
- SHA-256 EXE — 513ec525d782e29e00dd92b25f25623b3811a9b2fc5c51d95e1190bcc7fa0d3c.
- SHA-256 официального ZIP — 703e325bfeee582f874b06d054eaae55ab7c194645d1835411685aafa85da5ea.
- Исходный snapshot — mihomo-v1.19.34.tar.gz.
- SHA-256 snapshot — db0cc4145fe4024e9c2386bd4768bf23a69c8789610d56d7243819e2f77c0cfe.
- Лицензия ядра — Licenses/Mihomo-GPL-3.0.txt.

Официальный релиз — https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.34.

Соответствующие исходники для пересборки Windows с закреплёнными Go-зависимостями включены в mihomo-v1.19.34-corresponding-source.tar.gz. SHA-256 архива — 02d9c3fbcd456677c35129df1aeef2c429d09dc9d14cfb2332af6ae204354c57.
Модули проверены командой go mod verify. Их уведомления находятся в vendor и Licenses/Mihomo-Go.
Для пересборки используй Build-Mihomo.ps1 и официальный Go 1.26.11. Скрипт использует vendor. Результат пересборки зависит от toolchain и параметров сборки.
