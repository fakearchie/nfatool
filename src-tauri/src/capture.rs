
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

// Windows 11 paints a 1px border on every top-level window, frameless ones
// included. DWMWA_COLOR_NONE is documented as removing it but does NOT on 10.0.26200
// (the call returns Ok and a #1F2021 line stays); measured pixel-by-pixel. Painting
// it the app's own colour makes it invisible while keeping the resize edge.
// COLORREF is 0x00BBGGRR, so #191A1E becomes 0x001E1A19 - keep in step with --window.
const APP_BACKGROUND: u32 = 0x001E_1A19;

pub fn remove_system_border(window: &WebviewWindow) {
    use windows::Win32::Graphics::Dwm::{
        DwmSetWindowAttribute, DWMWA_BORDER_COLOR, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
    };

    let Ok(raw) = window.hwnd() else {
        return;
    };
    let hwnd = HWND(raw.0 as _);

    unsafe {
        let colour = APP_BACKGROUND;
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_BORDER_COLOR,
            &colour as *const u32 as *const core::ffi::c_void,
            std::mem::size_of::<u32>() as u32,
        );

        let corners = DWMWCP_ROUND;
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &corners as *const _ as *const core::ffi::c_void,
            std::mem::size_of::<i32>() as u32,
        );
    }
}

pub fn strip_border_on_main(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        remove_system_border(&window);
    }
}
