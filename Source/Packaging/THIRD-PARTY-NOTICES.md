# Сторонние компоненты MorphocyteOS

Лицензионные файлы входят в каталог `Licenses`. Эти уведомления описывают комплект и не заменяют тексты соответствующих лицензий.

## Mihomo 1.19.31

`mihomo.exe` — отдельный процесс, собранный из исходников MetaCubeX/mihomo commit `ab405bad5beeeac8b003bb01f60f134f6df54471` с Go 1.26.8 и тегом `with_gvisor`. Полное дерево commit, закреплённые исходники Go-зависимостей и инструкция пересборки находятся в `ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz`. Неизменённый upstream snapshot приложен отдельно как `mihomo-v1.19.31.tar.gz`.

Лицензия ядра: GNU GPL version 3 (`Licenses/Mihomo-GPL-3.0.txt`). Лицензии зависимостей и сохранённые уведомления: `Licenses/Mihomo-Go/`; они также остаются в vendored исходниках. Отдельные прямые уведомления: `Licenses/Mihomo-kcptun-MIT.txt`, `Licenses/Mihomo-faketcp-MIT.txt`, `Licenses/Mihomo-faketcp-Attribution.txt` и `Licenses/Mihomo-chacha-MIT.txt`.

SHA-256 поставляемого `mihomo.exe`:

`4a2275f385fc11106f7d819c16b510fcca5e4996b5e902d295bfc78ac29c5530`

## sing-box 1.14.2

`sing-box.exe` — неизменённое официальное Windows amd64 ядро из релиза SagerNet/sing-box v1.14.2. Оно запускается отдельным процессом, как альтернативный движок; MorphocyteOS не является продуктом SagerNet и не заявляет связи с upstream.

SHA-256 исполняемого файла: `7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410`.
SHA-256 официального ZIP: `c2d8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32`.

Лицензия: GPL-3.0-or-later с дополнительным условием upstream об имени и ассоциации. Уведомление находится в `Licenses/SingBox-LICENSE.txt`, полный GPL — в `Licenses/SingBox-GPL-3.0.txt`. Неизменённый snapshot исходников: `ThirdPartySource/sing-box-v1.14.2.tar.gz` (SHA-256 `67dd8f8c37ecaaadcfcafad1f0827eed4b034c963b86fd3aa5c0d7a36876845d`). Соответствующие исходники с закреплёнными Go-зависимостями для Windows находятся в `ThirdPartySource/sing-box-v1.14.2-corresponding-source.tar.gz` (SHA-256 `c2b3d9ae082a364aa082e120931b1df44e68e63aa27c7ca51201b4ae19660d75`), уведомления зависимостей — в `Licenses/SingBox-Go`. Контрольные суммы модулей проверены Go; из vendored-дерева исключены неиспользуемые готовые нативные библиотеки других платформ. Инструкция Windows-пересборки: `ThirdPartySource/Build-SingBox.ps1`; побитное совпадение с официальным бинарником не обещается.

Официальные ссылки: https://github.com/SagerNet/sing-box/releases/tag/v1.14.2 и https://github.com/SagerNet/sing-box/tree/v1.14.2.

`libcronet.dll` из upstream ZIP не входит в комплект: Naive outbound в этом адаптере не поддерживается.

## YamlDotNet 18.1.0

MIT, полный текст: `Licenses/YamlDotNet-18.1.0-MIT.txt`. Версия и commit подтверждены метаданными восстановленного NuGet-пакета; лицензия взята из upstream commit `748334a8fa7c227740018b284b71ad95cc6b7fc7`.

## .NET Runtime и Windows Desktop Runtime 10.0.12

Лицензии скопированы из соответствующих восстановленных runtime NuGet-пакетов:

- `Microsoft.NETCore.App.Runtime.win-x64/10.0.12`: `Licenses/DotNet-Runtime-10.0.12-MIT.txt` и `Licenses/DotNet-Runtime-10.0.12-ThirdPartyNotices.txt`.
- `Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12`: `Licenses/DotNet-WindowsDesktop-10.0.12-MIT.txt`.

## Само приложение

Исходный код MorphocyteOS распространяется по GNU GPL version 3. Полный текст — в файле `LICENSE` в корне исходного репозитория и в `LICENSE.txt` рядом с программой. Mihomo распространяется отдельно по собственной GPLv3; это уведомление не утверждает, что сторонние библиотеки .NET лицензированы GPL.

Copyright (C) 2026 Rabotaet3.
