use tao::{dpi::LogicalSize, window::Window};
use windows::{
    Win32::{Foundation::ERROR_SUCCESS, System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_BINARY, REG_OPTION_NON_VOLATILE, RRF_RT_REG_BINARY,
        RegCloseKey, RegCreateKeyExW, RegGetValueW, RegSetValueExW,
    }},
    core::{PCWSTR, w},
};

const KEY: PCWSTR = if cfg!(feature = "isolated-check") {
    w!(r"Software\MorphocyteOS\AtlasVerification")
} else {
    w!(r"Software\MorphocyteOS\Atlas")
};
const VALUE: PCWSTR = w!("Window");
pub const MIN_SIZE: LogicalSize<u32> = LogicalSize::new(590, 490);

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct Placement {
    pub size: LogicalSize<u32>,
    pub maximized: bool,
}

impl Placement {
    pub fn load() -> Self {
        let mut values = [0_u32; 3];
        let mut length = size_of_val(&values) as u32;
        let result = unsafe { RegGetValueW(HKEY_CURRENT_USER, KEY, VALUE, RRF_RT_REG_BINARY,
            None, Some(values.as_mut_ptr().cast()), Some(&mut length)) };
        match values {
            [width @ 590..=16_384, height @ 490..=16_384, maximized @ 0..=1]
                if result == ERROR_SUCCESS && length == size_of_val(&values) as u32 => Self {
                    size: LogicalSize::new(width, height), maximized: maximized == 1,
                },
            _ => Self { size: LogicalSize::new(1120, 770), maximized: false },
        }
    }

    pub fn capture(&mut self, window: &Window) {
        if window.is_minimized() { return; }
        self.maximized = window.is_maximized();
        if self.maximized { return; }
        let size = window.inner_size().to_logical(window.scale_factor());
        if size.width >= MIN_SIZE.width && size.height >= MIN_SIZE.height { self.size = size; }
    }

    pub fn persist(self, stored: &mut Self) {
        if self != *stored && self.save() { *stored = self; }
    }

    fn save(self) -> bool {
        let mut key = HKEY::default();
        let values = [self.size.width, self.size.height, u32::from(self.maximized)];
        let bytes = values.into_iter().flat_map(u32::to_le_bytes).collect::<Vec<_>>();
        unsafe {
            if RegCreateKeyExW(HKEY_CURRENT_USER, KEY, None, PCWSTR::null(), REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE, None, &mut key, None) != ERROR_SUCCESS { return false; }
            let result = RegSetValueExW(key, VALUE, None, REG_BINARY, Some(&bytes));
            let _ = RegCloseKey(key);
            result == ERROR_SUCCESS
        }
    }
}
