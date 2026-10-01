# Сторонние компоненты MorphocyteOS

Лицензионные файлы входят в каталог `Licenses`. Эти уведомления описывают комплект и не заменяют тексты соответствующих лицензий.

## Mihomo 1.19.31

`mihomo.exe` — отдельный процесс, собранный из исходников MetaCubeX/mihomo commit `ab405bad5beeeac8b003bb01f60f134f6df54471` с Go 1.26.8 и тегом `with_gvisor`. Полное дерево commit, закреплённые исходники Go-зависимостей и инструкция пересборки находятся в `ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz`. Неизменённый upstream snapshot приложен отдельно как `mihomo-v1.19.31.tar.gz`.

Лицензия ядра: GNU GPL version 3 (`Licenses/Mihomo-GPL-3.0.txt`). Лицензии зависимостей и сохранённые уведомления: `Licenses/Mihomo-Go/`; они также остаются в vendored исходниках. Отдельные прямые уведомления: `Licenses/Mihomo-kcptun-MIT.txt`, `Licenses/Mihomo-faketcp-MIT.txt`, `Licenses/Mihomo-faketcp-Attribution.txt` и `Licenses/Mihomo-chacha-MIT.txt`.

SHA-256 поставляемого `mihomo.exe`:

`4a2275f385fc11106f7d819c16b510fcca5e4996b5e902d295bfc78ac29c5530`

## YamlDotNet 18.1.0

MIT, полный текст: `Licenses/YamlDotNet-18.1.0-MIT.txt`. Версия и commit подтверждены метаданными восстановленного NuGet-пакета; лицензия взята из upstream commit `748334a8fa7c227740018b284b71ad95cc6b7fc7`.

## .NET Runtime и Windows Desktop Runtime 10.0.12

Лицензии скопированы из соответствующих восстановленных runtime NuGet-пакетов:

- `Microsoft.NETCore.App.Runtime.win-x64/10.0.12`: `Licenses/DotNet-Runtime-10.0.12-MIT.txt` и `Licenses/DotNet-Runtime-10.0.12-ThirdPartyNotices.txt`.
- `Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12`: `Licenses/DotNet-WindowsDesktop-10.0.12-MIT.txt`.

## Само приложение

Исходный код MorphocyteOS распространяется по GNU GPL version 3. Полный текст — в файле `LICENSE` в корне исходного репозитория и в `LICENSE.txt` рядом с программой. Mihomo распространяется отдельно по собственной GPLv3; это уведомление не утверждает, что сторонние библиотеки .NET лицензированы GPL.

Copyright (C) 2026 Rabotaet3.
