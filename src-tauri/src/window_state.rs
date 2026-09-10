
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::{Manager, PhysicalPosition, PhysicalSize};

// Bump when the layout's space needs change: a size saved by an older build is discarded.
// set_size is NOT bound by the window's minHeight, hence the clamp in size_to_restore.
const LAYOUT_VERSION: u32 = 3;

const MIN_WIDTH: u32 = 560;
const MIN_HEIGHT: u32 = 430;

#[derive(Serialize, Deserialize, Debug, Clone, Copy, PartialEq)]
pub struct WindowState {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    #[serde(default)]
    pub layout: u32,
}

fn state_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("window.json")
}

fn load() -> Option<WindowState> {
    serde_json::from_str(&fs::read_to_string(state_path()).ok()?).ok()
}

pub fn save(window: &tauri::WebviewWindow) {
    if window.is_minimized().unwrap_or(false) {
        return;
    }
    let (Ok(pos), Ok(size)) = (window.outer_position(), window.inner_size()) else {
        return;
    };
    if size.width == 0 || size.height == 0 {
        return;
    }
    let state = WindowState {
        x: pos.x,
        y: pos.y,
        width: size.width,
        height: size.height,
        layout: LAYOUT_VERSION,
    };
    let path = state_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(json) = serde_json::to_string(&state) {
        let _ = fs::write(path, json);
    }
}

type Rect = (i32, i32, i32, i32);

fn usable_on(state: &WindowState, (left, top, right, bottom): Rect) -> bool {
    const MARGIN: i32 = 80;
    let win_right = state.x + state.width as i32;
    let win_bottom = state.y + state.height as i32;

    let overlap_w = win_right.min(right) - state.x.max(left);
    let overlap_h = win_bottom.min(bottom) - state.y.max(top);

    overlap_w >= MARGIN && overlap_h >= MARGIN && state.y >= top - 8
}

fn is_visible_on_some_monitor(window: &tauri::WebviewWindow, state: &WindowState) -> bool {
    let Ok(monitors) = window.available_monitors() else {
        return false;
    };
    monitors.iter().any(|m| {
        let p = m.position();
        let s = m.size();
        usable_on(state, (p.x, p.y, p.x + s.width as i32, p.y + s.height as i32))
    })
}

fn size_to_restore(state: &WindowState) -> Option<(u32, u32)> {
    if state.layout != LAYOUT_VERSION {
        return None;
    }
    Some((state.width.max(MIN_WIDTH), state.height.max(MIN_HEIGHT)))
}

pub fn restore(app: &tauri::AppHandle) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let Some(state) = load() else {
        return;
    };
    if !is_visible_on_some_monitor(&window, &state) {
        return;
    }
    if let Some((width, height)) = size_to_restore(&state) {
        let _ = window.set_size(PhysicalSize::new(width, height));
    }
    let _ = window.set_position(PhysicalPosition::new(state.x, state.y));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_through_json() {
        let state = WindowState {
            x: 120,
            y: -40,
            width: 660,
            height: 360,
            layout: LAYOUT_VERSION,
        };
        let json = serde_json::to_string(&state).unwrap();
        assert_eq!(serde_json::from_str::<WindowState>(&json).unwrap(), state);
    }

    #[test]
    fn garbage_is_ignored_rather_than_panicking() {
        assert!(serde_json::from_str::<WindowState>("{}").is_err());
        assert!(serde_json::from_str::<WindowState>("not json").is_err());
    }

    const PRIMARY: Rect = (0, 0, 1920, 1080);
    const SECOND_LEFT: Rect = (-1920, 0, 0, 1080);

    fn at(x: i32, y: i32) -> WindowState {
        WindowState {
            x,
            y,
            width: 660,
            height: 360,
            layout: LAYOUT_VERSION,
        }
    }

    fn sized(width: u32, height: u32, layout: u32) -> WindowState {
        WindowState {
            x: 100,
            y: 100,
            width,
            height,
            layout,
        }
    }

    #[test]
    fn a_size_from_an_older_layout_is_discarded() {
        assert_eq!(size_to_restore(&sized(562, 341, 1)), None);
        assert_eq!(size_to_restore(&sized(562, 341, 0)), None);
    }

    #[test]
    fn an_unversioned_file_defaults_to_untrusted() {
        let legacy: WindowState =
            serde_json::from_str(r#"{"x":10,"y":20,"width":660,"height":340}"#).unwrap();
        assert_eq!(legacy.layout, 0);
        assert_eq!(size_to_restore(&legacy), None);
        assert_eq!((legacy.x, legacy.y), (10, 20));
    }

    #[test]
    fn a_current_size_is_restored_as_is() {
        assert_eq!(
            size_to_restore(&sized(900, 620, LAYOUT_VERSION)),
            Some((900, 620))
        );
    }

    #[test]
    fn a_too_small_size_is_clamped_to_the_minimum() {
        assert_eq!(
            size_to_restore(&sized(200, 100, LAYOUT_VERSION)),
            Some((MIN_WIDTH, MIN_HEIGHT))
        );
    }

    #[test]
    fn a_normal_position_is_usable() {
        assert!(usable_on(&at(600, 300), PRIMARY));
        assert!(usable_on(&at(0, 0), PRIMARY));
    }

    #[test]
    fn a_position_on_a_removed_monitor_is_rejected() {
        let orphan = at(-1500, 400);
        assert!(usable_on(&orphan, SECOND_LEFT));
        assert!(!usable_on(&orphan, PRIMARY));
    }

    #[test]
    fn a_sliver_on_screen_is_not_enough() {
        assert!(!usable_on(&at(1900, 300), PRIMARY));
        assert!(!usable_on(&at(300, 1070), PRIMARY));
    }

    #[test]
    fn a_window_above_the_top_edge_is_rejected() {
        assert!(!usable_on(&at(600, -200), PRIMARY));
        assert!(usable_on(&at(600, -8), PRIMARY));
    }

    #[test]
    fn straddling_two_monitors_is_fine() {
        let straddling = at(-200, 300);
        assert!(usable_on(&straddling, SECOND_LEFT) || usable_on(&straddling, PRIMARY));
    }
}
