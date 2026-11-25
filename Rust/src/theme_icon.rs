pub(crate) const fn theme_icon_id(theme: u16) -> u16 {
    // Keep resource IDs separate from the Shell's special -1 icon index.
    100 + theme
}
