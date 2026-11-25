from base64 import b64encode
from pathlib import Path
import json
import re
from html import escape

root = Path(__file__).parent
ui = root / "ui"
original = root / "Backend" / "Source" / "MorphocyteRouter"
source = (ui / "atlas-template.html").read_text(encoding="utf-8")
view, _, old_script = source.rpartition("  <script>")
assert "function renderGlobe()" in old_script
globe = old_script[old_script.index("      const globe ="):old_script.index("      function goTo(page)")].strip()
globe = re.sub(r"      function inspectCountry\(country\) \{.*?(?=      const pointers =)", "", globe, flags=re.S)
globe = globe.replace("      let drawerTrigger;\n", "")
globe_edits = {
    "const interpolate = geo.geoInterpolate(origin, cityData[server.place]);":
        "if (!server.position) return [];\n        const interpolate = geo.geoInterpolate(origin, server.position);",
    "const point = projectMarker(cityData[server.place], rotate, 1, cx, cy, radius);":
        "if (!server.position) continue;\n          const point = projectMarker(server.position, rotate, 1, cx, cy, radius);",
    "const point = projectMarker(cityData[server.place], rotate, 1, cx, cy, radius);\n          return":
        "if (!server.position) return { index, distance: Infinity };\n          const point = projectMarker(server.position, rotate, 1, cx, cy, radius);\n          return",
    "if (near.distance < 8)":
        "if (near && near.distance < 8)",
    "index === state.server": "index === current",
    "names.of(regionCodes[iso])": "countryLabel(regionCodes[iso])",
    'paintPin(points[0], "Москва", "right", "#dfede8", width, height);':
        'paintPin(projectMarker(origin, rotate, 1, cx, cy, radius), "Устройство", "right", mapColors.marker, width, height);',
    'if (!state.auto || state.active) paintPin(points.at(-1), previewRoute ? "" : selected().city, "left", state.active ? "#e1ffa6" : "#a5bfc5", width, height);':
        'if (points.length && (!state.auto || state.active)) paintPin(points.at(-1), previewRoute ? "" : selected().country, "left", state.active ? mapColors.accent : mapColors.marker, width, height);',
    'paintPin(pathPoints.at(-1), servers[state.pending].city, "left", "#e1ffa6", width, height);':
        'if (pathPoints.length) paintPin(pathPoints.at(-1), servers[state.pending].country, "left", mapColors.accent, width, height);',
    'ctx.strokeStyle = "#0b1822";': 'ctx.strokeStyle = mapColors.labelShadow;',
    '"#23556883"': 'mapColors.halo1', '"#1c899337"': 'mapColors.halo2', '"#1c899300"': 'mapColors.haloEnd',
    '"#b0cecc32"': 'mapColors.stars', '"#98cccc41"': 'mapColors.orbit', '"#98cccc26"': 'mapColors.outerRim',
    '"#2b5869"': 'mapColors.water1', '"#173748"': 'mapColors.water2', '"#081725"': 'mapColors.water3',
    '"#7ca19b"': 'mapColors.land1', '"#3e6469"': 'mapColors.land2', '"#21404c"': 'mapColors.land3',
    '"#a9c4bc92"': 'mapColors.shore', '"#acd7718b"': 'mapColors.available',
    '"#c6d8c675"': 'mapColors.border', '"#b8e1df2e"': 'mapColors.grid',
    '"#e9ffac69"': 'mapColors.selected', '"#e1f9ab"': 'mapColors.accent', '"#d5f69fb8"': 'mapColors.dot',
    '"#b4e4dcaf"': 'mapColors.rim1', '"#82c5c6a0"': 'mapColors.rim2', '"#28526138"': 'mapColors.rim3',
    '"#bcf47224"': 'mapColors.beamGlow', '"#c6f08578"': 'mapColors.beamHalo',
    '"#dcf9ac"': 'mapColors.accent', '"#ecffb9c5"': 'mapColors.dot',
}
for old, new in sorted(globe_edits.items(), key=lambda item: -len(item[0])):
    globe = globe.replace(old, new)
globe = globe.replace("for (const region of oblasts) path(region);", "path(borderView.border);")
globe = re.sub(r'        const region = oblasts.find\(.*?        const feature = countries.find',
               '        const feature = countries.find', globe, count=1, flags=re.S)

theme_source = (original / "ThemeManager.cs").read_text(encoding="utf-8")
themes = {}
for match in re.finditer(r'\["([^"]+)"\]\s*=\s*new\(\s*(.*?)\b(true|false)\)', theme_source, re.S):
    name, body, light = match.groups()
    colors = re.findall(r'"(#[A-Fa-f0-9]+)"', body)
    assert len(colors) == 32, (name, len(colors))
    solid = ["#" + x[-6:] for x in colors]
    themes[name] = {
        "light": light == "true", "canvas": solid[0], "title": solid[1], "panel": solid[2],
        "surface": solid[3], "input": solid[4], "popup": solid[5], "text": solid[6],
        "muted": solid[7], "accent": solid[9], "secondary": solid[10], "onAccent": solid[11],
        "border": solid[12], "borderStrong": solid[13], "hover": solid[14], "selected": solid[15],
        "danger": solid[17], "log": solid[19], "orbit": solid[21], "orbitFill": solid[22],
        "start": solid[23], "middle": solid[24], "end": solid[25], "glow": solid[26],
        "outlineMiddle": solid[27], "outlineEnd": solid[28]
    }
assert len(themes) == 9
icon_palettes = json.loads((ui / "icon-palettes.json").read_text(encoding="utf-8"))
assert list(themes) == list(icon_palettes), "Icon order must match the app themes."
for resource_id, (name, colors) in enumerate(icon_palettes.items(), start=1):
    themes[name]["logo"] = colors
    themes[name]["iconResource"] = resource_id

view = re.sub(r"    @import url\(.*?\);\n", "", view)
view = re.sub(r'(?m)^ *#morphocyte-atlas (?:\.a-window-reopen|\.a-gesture|\[data-window=)[^\n]*\n', "", view)
view = re.sub(r'(?m)^ *<div class="a-window-reopen"[^\n]*\n', "", view)
view = re.sub(r'<script type="application/json" id="atlas-oblasts">.*?</script>',
              lambda _: '<script type="application/json" id="atlas-border-view">'
              + (ui / "border-view.json").read_text(encoding="utf-8") + '</script>', view, count=1, flags=re.S)
view = view.replace('<span class="a-mark" aria-hidden="true"></span>', (ui / "morphocyte-mark.svg").read_text(encoding="utf-8"))
view = view.replace('<span>Морфоцит <small>OS</small></span>', '<span class="a-wordmark"><strong>Морфоцит <span>OS</span></strong></span>', 1)
for old, new in {
    "<p class=\"a-state\" data-status>VPN активен</p>": '<p class="a-state" data-status>Загрузка</p>',
    "<h1 data-state>Подключён</h1>": '<h1 data-state>Морфоцит</h1>',
    "<span class=\"a-endpoint-code\" data-code>NL</span>": '<span class="a-endpoint-code" data-code>—</span>',
    "<b data-city>Амстердам</b><small data-detail>Нидерланды · VLESS</small>": '<b data-city>Выбери профиль</b><small data-detail>Подписка или YAML</small>',
    "<span>Отключить</span>": "<span>Подключить</span>",
    '<figcaption class="a-gesture">Потяни Землю, чтобы повернуть</figcaption>': '',
    '<span data-stat="download">21,3</span> <small>Мбит/с</small>': '<span data-stat="download">—</span>',
    '<span data-stat="upload">1,4</span> <small>Мбит/с</small>': '<span data-stat="upload">—</span>',
    '<span data-stat="ping">71</span> <small>мс</small>': '<span data-stat="ping">—</span>',
    'Подключение из Москвы к серверу в Амстердаме.': 'Схема выбранного сервера. Положение устройства условное.',
    "<b>Подписка</b><small>12,8 из 100 ГБ · до 18 декабря</small>": '<b>Подписка</b><small data-home-subscription>Нет подписки</small>',
    'Подписка · 12,8 из 100 ГБ': 'Подписка',
    '<b>Моя подписка</b><small data-subscription-updated>Обновлена сегодня в 21:40</small>': '<b data-subscription-name>Моя подписка</b><small data-subscription-updated>Подписка не добавлена</small>',
    '<small>12,8 ГБ / 100 ГБ</small>': '<small data-usage-label></small><button type="button" class="a-button cursor-interaction" data-source>Ссылка</button>',
    'value="12.8" max="100" aria-label="Использовано 12,8 ГБ из 100 ГБ"': 'value="0" max="100" aria-label="Использование трафика"',
    '<span>Действует до 18 декабря</span><span>Использовано 12,8 ГБ из 100 ГБ</span>': '<span data-subscription-expiry></span><span data-subscription-announcement></span>',
    '<option>Домен</option><option>По слову</option><option>Процесс</option><option>IP</option>':
        '<option value="DOMAIN-SUFFIX">Весь домен</option><option value="DOMAIN">Точный домен</option><option value="DOMAIN-KEYWORD">По слову</option><option value="PROCESS-NAME">Процесс</option><option value="IP-CIDR">IP</option>',
    '<option>Напрямую</option><option>Через VPN</option><option>Блокировать</option>':
        '<option value="DIRECT">Напрямую</option><option value="VPN">Через VPN</option><option value="REJECT">Блокировать</option>',
    '<div data-window-content>': '<div data-atlas-error role="alert" hidden></div><div data-window-content>',
    '<span id="atlas-tun-label">Режим TUN</span>':
        '<span id="atlas-tun-label" class="a-sr">Режим TUN</span><span data-tunnel-caption aria-hidden="true">Режим TUN</span>',
}.items():
    view = view.replace(old, new)
view = re.sub(r'<label class="a-setting"><span>Прозрачность панелей.*?</label>',
              lambda _: (ui / "settings.html").read_text(encoding="utf-8"), view, count=1, flags=re.S)
view = view.replace('<h1 id="atlas-settings-title">Настройки</h1></div>',
                    '<h1 id="atlas-settings-title">Настройки</h1>'
                    '<button class="a-button a-apply" type="submit" form="atlas-preferences" data-preferences-save disabled>Сохранить</button></div>')
view = view.replace('class="a-scroll-work a-settings-grid" data-preferences',
                    'class="a-scroll-work a-settings-grid" id="atlas-preferences" data-preferences')
view = view.replace('<button class="a-button a-apply cursor-interaction" type="button" data-apply disabled>Применить</button>',
                    '<div class="a-tools a-route-tools"><button class="a-button cursor-interaction" type="button" data-classic>Папки и процессы</button>'
                    '<button class="a-button cursor-interaction" type="button" data-process-toggle aria-expanded="false" aria-controls="atlas-process-picker" hidden>Запущенные процессы</button>'
                    '<button class="a-button cursor-interaction" type="button" data-source>Ссылка подписки</button>'
                    '<button class="a-button a-apply cursor-interaction" type="button" data-apply disabled>Применить</button></div>')
view = view.replace('<dl class="a-metrics"', '<div class="a-tools" style="margin:8px 0 0">'
    '<button class="a-button cursor-interaction" data-restart type="button" hidden>Перезапустить ядро</button>'
    '</div><dl class="a-metrics"')
view = view.replace('</dl>', '</dl>'
    '<div class="a-speed"><div><small>Макс. скорость приёма</small><div class="a-speed-reading">'
    '<strong data-max-speed>—</strong><span class="a-speed-progress" data-speed-progress role="status" hidden></span></div></div>'
    '<button class="a-button" type="button" data-speed disabled>Проверить</button>'
    '<p class="a-speed-issue" data-speed-issue role="status" hidden></p></div>'
    '<p class="a-connection-issue" data-connection-issue hidden></p>', 1)
view = view.replace('<div class="a-tunnel">', '<button class="a-button" type="button" data-source>Ссылка подписки</button>'
                    '<div class="a-tunnel">', 1)
view = view.replace('<div class="a-process-picker"', '<div class="a-scroll-work a-routes-work"><div class="a-process-picker"', 1)
view = view.replace('data-rules-empty hidden>Список правил пуст</p>', 'data-rules-empty hidden>Список правил пуст</p></div>')
view = view.replace('<div class="a-connection-table">', '<div class="a-connection-table a-scroll-work">')
sort_button = '<button class="a-ping-order" type="button" data-ping-order data-direction="none"><span>По пингу</span><i aria-hidden="true"></i></button>'
ping_method = ('<select class="a-select a-ping-method" data-ping-method aria-label="Способ проверки серверов" hidden>'
               '<option value="vpn">Через VPN</option><option value="tcp">TCP</option><option value="icmp">ICMP</option></select>')
view = view.replace('<button class="a-button cursor-interaction" type="button" data-ping-all',
                    ping_method + '<button class="a-button cursor-interaction" type="button" data-ping-all')
view = view.replace('<span>Пинг от устройства</span>', sort_button, 1)
view = view.replace('<div class="a-server-worklist" data-full-server-scroll>',
                    '<div class="a-server-sort-row">' + sort_button + '</div>'
                    '<p class="a-ping-issue" data-ping-issue role="status" hidden></p><div class="a-server-worklist" data-full-server-scroll>')
view = view.replace('<div class="a-drawer-scroll" data-drawer-scroll>',
                    '<p class="a-ping-issue" data-ping-issue role="status" hidden></p><div class="a-drawer-scroll" data-drawer-scroll>')
faq_source = (original / "UtilityDialogs.cs").read_text(encoding="utf-8")
faq = re.findall(r'AddFaq\(body, "([^"]+)", "([^"]+)"\);', faq_source)
assert len(faq) >= 20
faq_answers = {
    "Как подключиться впервые?": "Открой «Ссылка подписки» на вкладке «Подключение» и вставь ссылку. Для YAML, ссылки на один сервер или JSON Xray открой «Настройки → VPN-профиль». Выбери сервер на глобусе или на вкладке «Серверы» и нажми «Подключить».",
    "Как обновить подписку и проверить серверы?": "На вкладке «Серверы» нажми «Обновить». Морфоцит сохранит правила и папки. Выбери способ проверки и нажми «Пинг». «Через VPN» проверяет сайт по HTTPS, TCP проверяет порт сервера, а ICMP проверяет ответ его адреса. Для TCP и ICMP останови VPN. Чтобы отсортировать список, нажми «По пингу».",
    "Как работают трей и автозапуск?": "Кнопка «−» сворачивает окно. Крестик убирает Морфоцит в трей. Чтобы остановить VPN и закрыть приложение, нажми «Выход» в меню трея. В настройках можно включить запуск при входе в Windows и подключение при входе или при открытии приложения.",
    "Что означают статус, пинг и скорость?": "«Приём» и «Отдача» показывают текущий трафик. «Проверить подключение» в журнале проверяет доступность HTTPS через VPN. Пинг на вкладке «Серверы» измеряется отдельно для каждого сервера.",
    "Что делать, если VPN не отвечает?": "Попробуй другой сервер или запусти «Проверить подключение» в журнале. Затем открой текстовый журнал, чтобы увидеть сообщения ядра.",
    "Как обновлять приложение?": "В настройках можно проверить обновления вручную или включить автоматическую проверку. Нажми «Установить», чтобы скачать обновление и перезапустить программу.",
}
classic_faq = {
    "Как работают папки и выделение?", "Как перемещать и удалять папки?",
    "Как отменить изменение правил?", "Как вернуть черновик после правки YAML?",
    "Что происходит после сна или смены сети?",
}
native_faq = {
    "Как обновить подписку и проверить серверы?": "Останови VPN и нажми «Обновить» на вкладке «Серверы». Морфоцит сохранит правила. Выбери способ проверки и нажми «Пинг». «Через VPN» проверяет сайт по HTTPS, TCP проверяет порт сервера, а ICMP проверяет ответ его адреса. Для сортировки нажми «По пингу».",
    "Как добавить целую программу?": "В «Маршрутах» нажми «Запущенные процессы». Отметь нужные приложения галочками или выбери диапазон с Shift. Выбери маршрут, нажми «Добавить выбранные», затем «Применить».",
    "Как изменить маршрут правила?": "Выбери маршрут в строке правила и нажми «Применить». Если VPN работает, сначала останови его.",
    "Изменения сохраняются автоматически?": "Настройки внешнего вида сохраняются между запусками. Для записи черновика правил останови VPN и нажми «Применить». Морфоцит проверит профиль ядрами перед записью.",
    "Что означают статус, пинг и скорость?": "«Приём» и «Отдача» показывают текущий трафик. «Задержка» показывает последний успешный пинг выбранного сервера. «Проверить» под показателями запускает замер максимальной скорости приёма через текущий сервер.",
}
entries = []
for title, original_answer in faq:
    if title == "Как перенести настройки?":
        continue
    answer = faq_answers.get(title, original_answer).replace("«Применить в YAML»", "«Применить»").replace("«Точное имя»", "«Точный домен»")
    if title in {"Как добавить целую программу?", "Как работают папки и выделение?", "Как перемещать и удалять папки?",
                 "Как отменить изменение правил?", "Как вернуть черновик после правки YAML?"}:
        answer = "Открой «Маршруты → Папки и процессы». " + original_answer
    if title == "Как изменить маршрут правила?":
        answer = "Выбери маршрут в строке правила и нажми «Применить». Для одновременного изменения нескольких правил открой «Папки и процессы»."
    classic = " data-classic" if title in classic_faq else ""
    native_answer = f' data-native-answer="{escape(native_faq[title], quote=True)}"' if title in native_faq else ""
    entries.append(f"<details{classic}><summary>{escape(title)}</summary><p{native_answer}>{escape(answer)}</p></details>")
view = re.sub(r'<div class="a-faq">.*?</div>', lambda _: '<div class="a-faq a-scroll-work">' + "".join(entries) + '</div>',
              view, count=1, flags=re.S)
view = view.replace('<nav aria-label="Разделы Морфоцита">',
                    '<nav aria-label="Разделы Морфоцита"><span class="a-nav-indicator" data-nav-indicator aria-hidden="true" hidden></span>', 1)
view = view.replace('<div data-window-content>', '<button class="a-update-strip" data-update-offer type="button" hidden>'
    '<span>↑</span><span data-update-offer-text></span><span>Посмотреть</span></button><div data-window-content>', 1)
view = view.replace('<div class="a-sr" data-announcement', (ui / "subscription.html").read_text(encoding="utf-8") +
                    (ui / "update.html").read_text(encoding="utf-8") +
                    '<div class="a-sr" data-announcement', 1)

styles = (ui / "native.css").read_text(encoding="utf-8")
app = (ui / "app.js").read_text(encoding="utf-8").replace("/* ATLAS_GLOBE */", globe, 1)
theme_data = json.dumps(themes, ensure_ascii=False, separators=(",", ":")).replace("<", "\\u003c")
flags = {file.stem: b64encode(file.read_bytes()).decode("ascii") for file in sorted((ui / "flags").glob("*.svg"))}
assert len(flags) >= 240
page = (
    '<!doctype html>\n<html lang="ru"><head><meta charset="utf-8">'
    '<meta name="viewport" content="width=device-width,initial-scale=1">'
    '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; script-src \'unsafe-inline\'; '
    'style-src \'unsafe-inline\'; img-src data:; form-action \'none\'; object-src \'none\'; base-uri \'none\'">'
    '<title>Морфоцит OS</title><style>html{background:var(--atlas-background,#11211e)}</style>'
    '<script>if(window.morphocyteStartup){document.documentElement.style.setProperty("--atlas-background",'
    'window.morphocyteStartup.background);document.documentElement.style.colorScheme=window.morphocyteStartup.scheme}</script>'
    '</head><body>\n' + view + '\n</section>\n'
    '<style>' + styles + '</style>\n'
    '<script type="application/json" id="atlas-flags">' + json.dumps(flags, separators=(",", ":")) + '</script>\n'
    '<script type="application/json" id="atlas-themes">' + theme_data + '</script>\n'
    '<script>\n' + app + '\n</script>\n</body></html>\n'
)
(ui / "index.html").write_text(page, encoding="utf-8")
print("Собран интерфейс Морфоцита. Тем:", len(themes), "Объём:", len(page.encode("utf-8")))
