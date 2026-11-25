use serde_json::Value;
use std::{cell::{Cell, RefCell}, ffi::c_void, ptr, rc::Rc, slice};
use windows::{
    Win32::{
        Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM},
        Graphics::Gdi::*,
        System::LibraryLoader::GetModuleHandleW,
        UI::{Controls::WM_MOUSELEAVE, Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
    },
    core::{PCWSTR, w},
};

const CLASS: PCWSTR = w!("MorphocyteOS.TrayPopup");
const COMMAND: u32 = WM_APP + 71;
pub(super) const CLOSED: u32 = WM_APP + 75;
const THEMES: usize = 7;
const BACK: usize = 8;
const ROOT: [usize; 6] = [1, 2, 3, 4, THEMES, 5];

#[derive(Clone)]
pub(super) struct Theme {
    pub resource: usize,
    name: String,
    background: [u32; 3],
    outline: [u32; 3],
    text: u32,
    hover: u32,
    border: u32,
    accent: u32,
    danger: u32,
}

pub(super) fn themes(catalog: &Value) -> Vec<Theme> {
    let mut result = catalog.as_object().into_iter().flatten().filter_map(|(name, palette)| {
        let resource = palette["iconResource"].as_u64()? as usize;
        let color = |key: &str, fallback| palette[key].as_str().and_then(|hex| hex.strip_prefix('#'))
            .and_then(|hex| u32::from_str_radix(hex, 16).ok()).unwrap_or(fallback);
        let accent = color("accent", 0x75d6b6);
        Some(Theme {
            resource, name: name.clone(),
            background: [color("start", 0x233542), color("middle", 0x162530), color("end", 0x101b23)],
            outline: [color("glow", accent), color("outlineMiddle", accent), color("outlineEnd", accent)],
            text: color("text", 0xe5f5ef), hover: color("hover", 0x243e37), border: color("border", 0x365853),
            accent, danger: color("danger", 0xff818f),
        })
    }).collect::<Vec<_>>();
    result.sort_by_key(|theme| theme.resource);
    result
}

pub(super) fn register(instance: HINSTANCE) -> bool {
    unsafe {
        let cursor = LoadCursorW(None, IDC_ARROW).unwrap_or_default();
        RegisterClassW(&WNDCLASSW { hInstance: instance, lpszClassName: CLASS, hCursor: cursor,
            lpfnWndProc: Some(window_proc), ..Default::default() }) != 0
    }
}

pub(super) struct Popup {
    tray: HWND,
    owner: HWND,
    themes: Rc<[Theme]>,
    theme: Theme,
    online: bool,
    point: POINT,
    work: RECT,
    dpi: i32,
    variants: Cell<bool>,
    hot: Cell<Option<usize>>,
    pressed: Cell<Option<usize>>,
    font: HFONT,
    frame: RefCell<Option<Frame>>,
}

struct Frame {
    surface: Surface,
    resting: Vec<u32>,
    variants: bool,
}

#[derive(Clone, Copy)]
struct Layout {
    size: SIZE,
    frame: RECT,
    row: i32,
    padding: i32,
    divider: i32,
}

impl Layout {
    fn row(self, index: usize) -> RECT {
        let offset = if index >= 5 { self.divider } else { 0 };
        let top = self.frame.top + self.padding + index as i32 * self.row + offset;
        RECT { left: self.frame.left + self.padding, top,
            right: self.frame.right - self.padding, bottom: top + self.row }
    }
}

impl Popup {
    pub(super) fn show(tray: HWND, owner: HWND, themes: Rc<[Theme]>, resource: usize,
        online: bool, point: POINT, dpi: u32) -> windows::core::Result<HWND> {
        let theme = themes.iter().find(|entry| entry.resource == resource).or_else(|| themes.first())
            .ok_or_else(|| windows::core::Error::from_win32())?.clone();
        let mut monitor = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        unsafe {
            let screen = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            if !GetMonitorInfoW(screen, &mut monitor).as_bool() { return Err(windows::core::Error::from_win32()); }
            let dpi = dpi.max(96) as i32;
            let font = CreateFontW(-((13 * dpi + 48) / 96), 0, 0, 0, 500, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, 0, w!("Segoe UI"));
            let popup = Rc::new(Self { tray, owner, themes, theme, online, point, work: monitor.rcWork, dpi,
                variants: Cell::new(false), hot: Cell::new(None), pressed: Cell::new(None), font,
                frame: RefCell::new(None) });
            let instance = HINSTANCE(GetModuleHandleW(PCWSTR::null())?.0);
            let window = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, CLASS,
                w!("Меню Морфоцита"), WS_POPUP, 0, 0, 0, 0, Some(tray), None, Some(instance), None)?;
            SetWindowLongPtrW(window, GWLP_USERDATA, Rc::into_raw(Rc::clone(&popup)) as isize);
            if let Err(error) = popup.render(window) {
                let _ = DestroyWindow(window);
                return Err(error);
            }
            let _ = ShowWindow(window, SW_SHOW);
            let _ = SetForegroundWindow(window);
            let _ = SetFocus(Some(window));
            Ok(window)
        }
    }

    fn px(&self, logical: i32) -> i32 { (logical * self.dpi + 48) / 96 }
    fn count(&self) -> usize { if self.variants.get() { self.themes.len() + 1 } else { ROOT.len() } }

    fn layout(&self) -> Layout {
        let pad = self.px(8);
        let shadow = self.px(12);
        let divider = if self.variants.get() { 0 } else { self.px(11) };
        let count = self.count() as i32;
        let max_area = self.work.bottom - self.work.top - shadow * 2;
        let row = self.px(if self.variants.get() { 37 } else { 44 })
            .min(((max_area - 2 * pad - divider) / count).max(self.px(22)));
        let width = self.px(238).min(self.work.right - self.work.left);
        let height = row * count + 2 * pad + divider;
        Layout { size: SIZE { cx: width + 2 * shadow, cy: height + 2 * shadow },
            frame: RECT { left: shadow, top: shadow, right: shadow + width, bottom: shadow + height },
            row, padding: pad, divider }
    }

    fn label(&self, command: usize) -> &str {
        match command {
            1 => "Открыть MorphocyteOS",
            2 if self.online => "Остановить VPN",
            2 => "Запустить VPN",
            3 => "Настройки",
            4 => "Журнал",
            5 => "Выход",
            THEMES => "Тема",
            BACK => "Назад",
            _ => "",
        }
    }

    fn command(&self, index: usize) -> usize {
        if self.variants.get() {
            if index == 0 { BACK } else { self.themes[index - 1].resource + 100 }
        } else { ROOT[index] }
    }

    fn hit(&self, value: LPARAM) -> Option<usize> {
        let x = value.0 as i16 as i32;
        let y = (value.0 >> 16) as i16 as i32;
        let layout = self.layout();
        (0..self.count()).find(|index| {
            let row = layout.row(*index);
            x >= row.left && x < row.right && y >= row.top && y < row.bottom
        })
    }

    fn render(&self, hwnd: HWND) -> windows::core::Result<()> {
        let variants = self.variants.get();
        // Window updates can send messages synchronously to this same window.
        let Ok(mut frame) = self.frame.try_borrow_mut() else { return Ok(()); };
        let layout = self.layout();
        let bounds = layout.frame;
        let frame_width = bounds.right - bounds.left;
        let frame_height = bounds.bottom - bounds.top;
        let left = (self.point.x - self.px(18)).clamp(self.work.left, (self.work.right - frame_width).max(self.work.left));
        let above = self.point.y - frame_height - self.px(4);
        let top = if above >= self.work.top { above } else { self.point.y + self.px(8) }
            .clamp(self.work.top, (self.work.bottom - frame_height).max(self.work.top));
        let position = POINT { x: left - bounds.left, y: top - bounds.top };
        if frame.as_ref().is_none_or(|frame| frame.variants != variants) {
            let mut surface = Surface::new(layout.size)?;
            self.paint_frame(&mut surface, layout);
            unsafe {
                let _ = SelectObject(surface.dc, HGDIOBJ(self.font.0));
                SetBkMode(surface.dc, TRANSPARENT);
            }
            for index in 0..self.count() {
                self.paint_item(&mut surface, layout, index, false);
            }
            let resting = surface.pixels().to_vec();
            *frame = Some(Frame { surface, resting, variants });
        }
        let Frame { surface, resting, .. } = frame.as_mut().expect("tray frame built above");
        surface.pixels().copy_from_slice(resting);
        if let Some(index) = self.hot.get() {
            self.paint_item(surface, layout, index, true);
        }
        #[cfg(feature = "isolated-check")]
        surface.save_preview(self.theme.resource, variants, layout.size);
        unsafe {
            let blending = BLENDFUNCTION { BlendOp: AC_SRC_OVER as u8, SourceConstantAlpha: 255,
                AlphaFormat: AC_SRC_ALPHA as u8, ..Default::default() };
            UpdateLayeredWindow(hwnd, None, Some(&position), Some(&layout.size), Some(surface.dc),
                Some(&POINT::default()), COLORREF(0), Some(&blending), ULW_ALPHA)
        }
    }

    fn paint_item(&self, surface: &mut Surface, layout: Layout, index: usize, hot: bool) {
        let command = self.command(index);
        let preview = (self.variants.get() && index > 0).then(|| &self.themes[index - 1]);
        let theme = preview.unwrap_or(&self.theme);
        let row = layout.row(index);
        if hot || preview.is_some() {
            let button = if preview.is_some() {
                RECT { top: row.top + self.px(2), bottom: row.bottom - self.px(2), ..row }
            } else { row };
            self.paint_button(surface, layout, button, theme, preview.is_some(), hot);
        }
        let label = RECT { left: row.left + self.px(if self.variants.get() { 26 } else { 13 }),
            right: row.right - self.px(26), ..row };
        let name = preview.map_or_else(|| self.label(command), |theme| theme.name.as_str());
        unsafe {
            draw_text(surface.dc, label, name, if command == 5 { theme.danger } else { theme.text }, false);
            if command == BACK {
                let marker = RECT { left: row.left + self.px(8), right: row.left + self.px(23), ..row };
                draw_text(surface.dc, marker, "‹", theme.accent, true);
            } else if command == THEMES || command == self.theme.resource + 100 {
                let marker = RECT { left: row.right - self.px(25), right: row.right - self.px(12), ..row };
                draw_text(surface.dc, marker, if command == THEMES { "›" } else { "✓" }, theme.accent, true);
            }
            let _ = GdiFlush();
        }
        // GDI text may clear the alpha byte within the opaque panel.
        let pixels = surface.pixels();
        for y in row.top..row.bottom {
            let start = (y * layout.size.cx + row.left + self.px(4)) as usize;
            let end = (y * layout.size.cx + row.right - self.px(4)) as usize;
            for pixel in &mut pixels[start..end] { *pixel |= 0xff000000; }
        }
    }

    fn paint_button(&self, surface: &mut Surface, layout: Layout, bounds: RECT, theme: &Theme, colored: bool, hot: bool) {
        let radius = self.px(8) as f32;
        let line = self.px(1) as f32;
        let width = (bounds.right - bounds.left) as f32;
        let height = (bounds.bottom - bounds.top) as f32;
        let stroke = if hot { theme.accent } else { mix(theme.border, theme.accent, 0.56) };
        let swatch_x = (bounds.left + self.px(13)) as f32;
        let swatch_y = (bounds.top + bounds.bottom) as f32 * 0.5;
        let swatch_radius = self.px(3) as f32;
        let pixels = surface.pixels();
        for y in bounds.top..bounds.bottom {
            for x in bounds.left..bounds.right {
                let distance = rounded_distance(bounds, radius, x as f32 + 0.5, y as f32 + 0.5);
                if distance >= 0.5 { continue; }
                let fraction = ((x - bounds.left) as f32 / width + (y - bounds.top) as f32 / height) * 0.5;
                let fill = if colored {
                    let base = gradient(theme.background, fraction, 0.53);
                    if hot { mix(base, theme.hover, 0.65) } else { base }
                } else { theme.hover };
                let mut color = mix(stroke, fill, (0.5 - distance - line).clamp(0., 1.));
                if colored {
                    let dot = (x as f32 + 0.5 - swatch_x).hypot(y as f32 + 0.5 - swatch_y);
                    color = mix(color, theme.accent, (swatch_radius + 0.5 - dot).clamp(0., 1.));
                }
                let pixel = &mut pixels[(y * layout.size.cx + x) as usize];
                *pixel = mix(*pixel, color, (0.5 - distance).min(1.0)) | 0xff000000;
            }
        }
    }

    fn paint_frame(&self, surface: &mut Surface, layout: Layout) {
        let bounds = layout.frame;
        let radius = self.px(15) as f32;
        let shadow = bounds.left as f32;
        let w = (bounds.right - bounds.left) as f32;
        let h = (bounds.bottom - bounds.top) as f32;
        let divider = layout.row(5).top - self.px(6);
        let width = layout.size.cx as usize;
        for (index, pixel) in surface.pixels().iter_mut().enumerate() {
            let x = (index % width) as f32 + 0.5;
            let y = (index / width) as f32 + 0.5;
            let distance = rounded_distance(bounds, radius, x, y);
            let opacity = (0.5 - distance).clamp(0., 1.);
            let shade = rounded_distance(bounds, radius, x, y - self.px(2) as f32);
            let shade = (1.0 - shade.max(0.0) / shadow).clamp(0., 1.).powi(2) * 0.26;
            if opacity == 0.0 { *pixel = argb(0, shade); continue; }
            let position = (((x - bounds.left as f32) / w + (y - bounds.top as f32) / h) * 0.5).clamp(0., 1.);
            let mut color = mix(gradient(self.theme.outline, position, 0.58), gradient(self.theme.background, position, 0.53),
                (0.5 - distance - self.px(1) as f32).clamp(0., 1.));
            if !self.variants.get() && (y as i32) == divider && x > (bounds.left + self.px(19)) as f32
                && x < (bounds.right - self.px(19)) as f32 { color = self.theme.border; }
            *pixel = argb(color, opacity + shade * (1.0 - opacity));
        }
    }
}

impl Drop for Popup {
    fn drop(&mut self) {
        self.frame.get_mut().take();
        let _ = unsafe { DeleteObject(HGDIOBJ(self.font.0)) };
    }
}

unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe {
        let raw = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Popup;
        if raw.is_null() { return DefWindowProcW(hwnd, message, wparam, lparam); }
        if message == WM_NCDESTROY {
            let popup = Rc::from_raw(raw);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            let _ = PostMessageW(Some(popup.tray), CLOSED, WPARAM(hwnd.0 as usize), LPARAM(0));
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        // Keep the state alive if a window API closes the menu during this message.
        Rc::increment_strong_count(raw);
        let popup = Rc::from_raw(raw);
        let mut chosen = None;
        match message {
            WM_MOUSEMOVE => {
                let item = popup.hit(lparam);
                if popup.hot.replace(item) != item {
                    let mut tracking = TRACKMOUSEEVENT { cbSize: size_of::<TRACKMOUSEEVENT>() as u32,
                        dwFlags: TME_LEAVE, hwndTrack: hwnd, dwHoverTime: 0 };
                    let _ = TrackMouseEvent(&mut tracking);
                    let _ = popup.render(hwnd);
                }
            }
            WM_MOUSELEAVE => {
                if popup.hot.replace(None).is_some() { let _ = popup.render(hwnd); }
            }
            WM_LBUTTONDOWN => {
                popup.pressed.set(popup.hit(lparam));
                let _ = SetCapture(hwnd);
            }
            WM_LBUTTONUP => {
                if GetCapture() == hwnd { let _ = ReleaseCapture(); }
                if let Some(index) = popup.pressed.replace(None).filter(|index| Some(*index) == popup.hit(lparam)) {
                    chosen = Some(popup.command(index));
                }
            }
            WM_KEYDOWN => {
                let key = VIRTUAL_KEY(wparam.0 as u16);
                let count = popup.count();
                match key {
                    VK_ESCAPE | VK_LEFT if popup.variants.get() => chosen = Some(BACK),
                    VK_ESCAPE => chosen = Some(0),
                    VK_DOWN | VK_TAB | VK_UP | VK_HOME | VK_END => {
                        let previous = popup.hot.get();
                        let backwards = key == VK_UP || (key == VK_TAB && GetKeyState(i32::from(VK_SHIFT.0)) < 0);
                        let next = match key {
                            VK_HOME => 0,
                            VK_END => count - 1,
                            _ if backwards => previous.map_or(count - 1, |row| (row + count - 1) % count),
                            _ => previous.map_or(0, |row| (row + 1) % count),
                        };
                        if popup.hot.replace(Some(next)) != Some(next) { let _ = popup.render(hwnd); }
                    }
                    VK_RETURN | VK_SPACE => chosen = Some(popup.command(popup.hot.get().unwrap_or(0))),
                    VK_RIGHT if popup.hot.get().is_some_and(|row| popup.command(row) == THEMES) => chosen = Some(THEMES),
                    _ => {}
                }
            }
            WM_ACTIVATE if wparam.0 as u16 == WA_INACTIVE as u16 => chosen = Some(0),
            WM_CLOSE | WM_CANCELMODE => chosen = Some(0),
            _ => return DefWindowProcW(hwnd, message, wparam, lparam),
        }
        match chosen {
            Some(id @ (THEMES | BACK)) => {
                popup.variants.set(id == THEMES);
                popup.hot.set(None);
                let _ = popup.render(hwnd);
            }
            Some(id) => {
                let owner = popup.owner;
                let _ = DestroyWindow(hwnd);
                if id > 0 { let _ = PostMessageW(Some(owner), COMMAND, WPARAM(id), LPARAM(0)); }
            }
            None => {}
        }
        LRESULT(0)
    }
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    original: HGDIOBJ,
    data: *mut u32,
    len: usize,
}

impl Surface {
    fn new(size: SIZE) -> windows::core::Result<Self> {
        let info = BITMAPINFO { bmiHeader: BITMAPINFOHEADER { biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: size.cx, biHeight: -size.cy, biPlanes: 1, biBitCount: 32, biCompression: BI_RGB.0,
            ..Default::default() }, ..Default::default() };
        unsafe {
            let dc = CreateCompatibleDC(None);
            if dc.is_invalid() { return Err(windows::core::Error::from_win32()); }
            let mut bits: *mut c_void = ptr::null_mut();
            let bitmap = match CreateDIBSection(Some(dc), &info, DIB_RGB_COLORS, &mut bits, None, 0) {
                Ok(bitmap) => bitmap,
                Err(error) => { let _ = DeleteDC(dc); return Err(error); }
            };
            let original = SelectObject(dc, HGDIOBJ(bitmap.0));
            Ok(Self { dc, bitmap, original, data: bits.cast(), len: (size.cx * size.cy) as usize })
        }
    }

    fn pixels(&mut self) -> &mut [u32] { unsafe { slice::from_raw_parts_mut(self.data, self.len) } }

    #[cfg(feature = "isolated-check")]
    fn save_preview(&mut self, resource: usize, variants: bool, size: SIZE) {
        let Some(directory) = std::env::var_os("ATLAS_TRAY_PREVIEW") else { return; };
        let page = if variants { "themes" } else { "main" };
        let path = std::path::PathBuf::from(directory).join(format!("tray-{resource}-{page}.png"));
        let Ok(file) = std::fs::File::create(path) else { return; };
        let mut encoder = png::Encoder::new(file, size.cx as u32, size.cy as u32);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let Ok(mut writer) = encoder.write_header() else { return; };
        let rgba = self.pixels().iter().flat_map(|pixel| {
            let [blue, green, red, alpha] = pixel.to_le_bytes();
            [red, green, blue, alpha]
        }).collect::<Vec<_>>();
        let _ = writer.write_image_data(&rgba);
    }
}

impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.original);
            let _ = DeleteDC(self.dc);
            let _ = DeleteObject(HGDIOBJ(self.bitmap.0));
        }
    }
}

fn rounded_distance(r: RECT, radius: f32, x: f32, y: f32) -> f32 {
    let half_w = (r.right - r.left) as f32 * 0.5;
    let half_h = (r.bottom - r.top) as f32 * 0.5;
    let dx = (x - r.left as f32 - half_w).abs() - (half_w - radius);
    let dy = (y - r.top as f32 - half_h).abs() - (half_h - radius);
    dx.max(0.0).hypot(dy.max(0.0)) + dx.max(dy).min(0.0) - radius
}

fn mix(a: u32, b: u32, fraction: f32) -> u32 {
    [16, 8, 0].into_iter().map(|shift| {
        let start = ((a >> shift) & 255) as f32;
        let end = ((b >> shift) & 255) as f32;
        ((start + (end - start) * fraction).round() as u32) << shift
    }).fold(0, |rgb, component| rgb | component)
}

fn gradient(stops: [u32; 3], position: f32, middle: f32) -> u32 {
    if position <= middle { mix(stops[0], stops[1], position / middle) }
    else { mix(stops[1], stops[2], (position - middle) / (1.0 - middle)) }
}

fn argb(color: u32, alpha: f32) -> u32 { mix(0, color, alpha) | ((alpha * 255.).round() as u32) << 24 }

unsafe fn draw_text(dc: HDC, mut bounds: RECT, text: &str, color: u32, center: bool) {
    let mut text = text.encode_utf16().collect::<Vec<_>>();
    unsafe {
        SetTextColor(dc, COLORREF(color.swap_bytes() >> 8));
        DrawTextW(dc, &mut text, &mut bounds, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS |
            DT_NOPREFIX | if center { DT_CENTER } else { DT_LEFT });
    }
}
