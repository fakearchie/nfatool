// Keeps the window out of screen recordings and screenshots.
//
// `WDA_EXCLUDEFROMCAPTURE` makes the desktop compositor omit the window from any
// capture API — OBS, Discord screen share, Snipping Tool, PrintScreen — while it
// stays fully visible on the physical monitor. It is the same flag Windows uses
// for DRM-protected video surfaces.
//
// The reference tool notes that a translucent window background defeats this. Our
// window is opaque (`backgroundColor` is set in tauri.conf.json and `body` paints
// a solid colour), so we are not in that case.

use tauri::{Manager, WebviewWindow};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    SetWindowDisplayAffinity, WDA_EXCLUDEFROMCAPTURE, WDA_NONE,
};

/// Applies the current preference to a window.
pub fn apply(window: &WebviewWindow, hide: bool) -> Result<(), String> {
    // Tauri and this crate may compile against different `windows` versions, so go
    // through the raw handle rather than passing their HWND type across.
    let raw = window
        .hwnd()
        .map_err(|e| format!("Could not get the window handle: {e}"))?;
    let hwnd = HWND(raw.0 as _);

    let affinity = if hide {
        WDA_EXCLUDEFROMCAPTURE
    } else {
        WDA_NONE
    };

    unsafe {
        SetWindowDisplayAffinity(hwnd, affinity)
            .map_err(|e| format!("SetWindowDisplayAffinity failed: {e}"))
    }
}

/// Applies the preference to the main window, if it exists. Best-effort: failing
/// to hide the window is never a reason to stop the app from running.
pub fn apply_to_main(app: &tauri::AppHandle, hide: bool) {
    if let Some(window) = app.get_webview_window("main") {
        if let Err(e) = apply(&window, hide) {
            crate::log::record(format!("hide-from-capture: {e}"));
        }
    }
}
