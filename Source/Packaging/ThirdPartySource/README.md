# Ядра MorphocyteOS — исходники и происхождение

## sing-box 1.14.4

Поставляется неизменённый официальный Windows amd64 бинарник sing-box.exe из sing-box-1.14.4-windows-amd64.zip. Он работает отдельным процессом. Версия Go официального бинарника — 1.26.10.

- Upstream commit — c005f35300ce76427dc285481ec5695f5b179cd2.
- SHA-256 EXE — 8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9.
- SHA-256 официального ZIP — 99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df.
- Исходный snapshot — sing-box-v1.14.4.tar.gz.
- SHA-256 snapshot — 1c4d2b3bf261a8d98e1b026be0bbcddbcb3f53bbca77eb15e8f25f7d4e602e59.
- Лицензия ядра — Licenses/SingBox-LICENSE.txt.

Официальный релиз — https://github.com/SagerNet/sing-box/releases/tag/v1.14.4.

Соответствующие исходники для пересборки Windows с закреплёнными Go-зависимостями включены в sing-box-v1.14.4-corresponding-source.tar.gz. SHA-256 архива — c0315f9bd59d62b570bd0ad19ade21c4f4f541be3fe5660a73ebfdcece787368.
Модули проверены командой go mod verify. Их уведомления находятся в vendor и Licenses/SingBox-Go.
Для пересборки используй Build-SingBox.ps1 и официальный Go 1.26.10. Скрипт использует vendor. Результат пересборки зависит от toolchain и параметров сборки.

Из vendored-дерева удалены готовые статические и динамические библиотеки Cronet для других ОС. Исходный код и уведомления сохранены. В программе отсутствует поддержка Naive outbound, libcronet.dll отдельно не поставляется.

## Xray 26.11.9

Поставляется неизменённый официальный Windows amd64 бинарник xray.exe из Xray-windows-64.zip. Он работает отдельным процессом. Версия Go официального бинарника — 1.27.3.

- Upstream commit — 676eaf66f8f05d4d558ee7a74b4736623d3feeb9.
- SHA-256 EXE — 1ca7a2cf61ba2c5d20b21a80d405b57933939751081cb289f07bd8ddf7cc3185.
- SHA-256 официального ZIP — 1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d.
- Исходный snapshot — xray-v26.11.9.tar.gz.
- SHA-256 snapshot — adccbbb35f9efccae0884bfd46b9d439daee60aa543efa19e775799c74589bab.
- Лицензия ядра — Licenses/Xray-MPL-2.0.txt.

Официальный релиз — https://github.com/XTLS/Xray-core/releases/tag/v26.11.9.

Xray использует локальные SOCKS-входы и подключения к VPN-серверам. Геоданные, Wintun и запускающие скрипты из upstream ZIP в комплект MorphocyteOS не входят.

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
