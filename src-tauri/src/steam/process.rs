use std::ffi::OsStr;
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::Command;
use std::time::Duration;

use winreg::RegKey;

use crate::settings::AppSettings;

// Prevents a console window flashing when spawning taskkill.
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

// Lets Steam run independently of this app.
const DETACHED_PROCESS: u32 = 0x0000_0008;

const ERR_STEAM_CLOSED: &str = "C000009A";

fn silent_command(program: impl AsRef<OsStr>) -> Command {
    let mut cmd = Command::new(program);
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

pub(crate) fn stop_steam() -> Result<(), String> {
    // Both kills are best-effort. taskkill's failure text is localised — a German
    // Windows reports "wurde nicht gefunden" for an already-dead process — so we
    // never parse stderr. Whether Steam is actually gone is the only thing that
    // matters, and we check that directly.
    kill_steam_by_pid();
    kill_steam_by_name();
    if wait_until_gone() {
        return Ok(());
    }

    // Surviving a normal kill means Steam is running elevated. Rather than make
    // this whole app require administrator — which breaks WebView2 — escalate
    // just the kill. The user sees one UAC prompt, and only in this case.
    kill_steam_elevated();
    if wait_until_gone() {
        return Ok(());
    }

    Err("Steam is still running and could not be closed. Close Steam manually, then try again."
        .to_string())
}

fn wait_until_gone() -> bool {
    for _ in 0..15 {
        if !steam_is_running() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(200));
    }
    false
}

/// Runs taskkill itself elevated via the shell's "runas" verb. Declining the UAC
/// prompt simply leaves Steam running, which the caller reports as a plain error.
fn kill_steam_elevated() {
    let script = "$ErrorActionPreference='SilentlyContinue'; \
foreach ($p in 'steam.exe','steamwebhelper.exe') { \
Start-Process -FilePath 'taskkill' -ArgumentList '/F','/IM',$p,'/T' \
-Verb RunAs -WindowStyle Hidden -Wait }";

    let _ = silent_command("powershell")
        .args(["-NoProfile", "-Command", script])
        .output();
}

/// Image names are not localised, so looking for "steam.exe" in tasklist output
/// works on any Windows language.
fn steam_is_running() -> bool {
    for process in ["steam.exe", "steamwebhelper.exe"] {
        let running = silent_command("tasklist")
            .args(["/FI", &format!("IMAGENAME eq {process}"), "/NH"])
            .output()
            .map(|o| {
                String::from_utf8_lossy(&o.stdout)
                    .to_lowercase()
                    .contains(process)
            })
            .unwrap_or(false);
        if running {
            return true;
        }
    }
    false
}

fn kill_steam_by_pid() {
    let hkcu = RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let Ok(steam_key) = hkcu.open_subkey("SOFTWARE\\Valve\\Steam\\ActiveProcess") else {
        return;
    };
    let Ok(pid) = steam_key.get_value::<u32, _>("pid") else {
        return;
    };
    if pid == 0 {
        return;
    }

    let killed = silent_command("taskkill")
        .args(["/F", "/PID", &pid.to_string(), "/T"])
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false);
    if killed {
        std::thread::sleep(Duration::from_millis(800));
    }
}

fn kill_steam_by_name() {
    for process in ["steam.exe", "steamwebhelper.exe"] {
        let killed = silent_command("taskkill")
            .args(["/F", "/IM", process, "/T"])
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false);
        if killed {
            std::thread::sleep(Duration::from_millis(500));
        }
    }
}

pub(crate) fn launch_steam(steam_path: &str, settings: &AppSettings) -> Result<(), String> {
    let exe = Path::new(steam_path).join("steam.exe");
    if !exe.exists() {
        return Err(ERR_STEAM_CLOSED.to_string());
    }
    let mut cmd = Command::new(&exe);
    if settings.launch_steam_minimized {
        cmd.arg("-silent");
    }
    if settings.launch_cs2_on_login {
        // Steam queues the launch until it has finished signing in, so this works
        // on the same invocation rather than needing a second, timed one.
        cmd.arg("-applaunch").arg(crate::steam::cs2::APPID);
    }
    cmd.creation_flags(DETACHED_PROCESS)
        .spawn()
        .map_err(|_| ERR_STEAM_CLOSED.to_string())?;
    Ok(())
}

pub(crate) fn write_autologin_user(account_name: &str) -> Result<(), String> {
    let hkcu = RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let steam_key = hkcu
        .open_subkey_with_flags("SOFTWARE\\Valve\\Steam", winreg::enums::KEY_SET_VALUE)
        .map_err(reg_error)?;
    steam_key
        .set_value("AutoLoginUser", &account_name)
        .map_err(reg_error)?;
    steam_key.set_value("RememberPassword", &1u32).map_err(reg_error)?;
    Ok(())
}

pub(crate) fn clear_autologin_if_matches(account_name: &str) {
    let hkcu = RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    if let Ok(key) = hkcu.open_subkey_with_flags(
        "SOFTWARE\\Valve\\Steam",
        winreg::enums::KEY_QUERY_VALUE | winreg::enums::KEY_SET_VALUE,
    ) {
        let current: String = key.get_value("AutoLoginUser").unwrap_or_default();
        if current == account_name {
            let _ = write_autologin_user("");
        }
    }
}

fn reg_error(e: std::io::Error) -> String {
    format!("{:08X}", e.raw_os_error().unwrap_or(0) as u32)
}
