
use tauri::{Manager, WebviewWindow};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    SetWindowDisplayAffinity, WDA_EXCLUDEFROMCAPTURE, WDA_NONE,
};

pub fn apply(window: &WebviewWindow, hide: bool) -> Result<(), String> {
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

pub fn apply_to_main(app: &tauri::AppHandle, hide: bool) {
    if let Some(window) = app.get_webview_window("main") {
        if let Err(e) = apply(&window, hide) {
            crate::log::record(format!("hide-from-capture: {e}"));
        }
    }
}
