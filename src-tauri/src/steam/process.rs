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
    kill_steam_by_pid()?;
    kill_steam_by_name()
}

fn kill_steam_by_pid() -> Result<(), String> {
    let hkcu = RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let steam_key = match hkcu.open_subkey("SOFTWARE\\Valve\\Steam\\ActiveProcess") {
        Ok(key) => key,
        Err(_) => return Ok(()),
    };
    let pid: u32 = match steam_key.get_value("pid") {
        Ok(pid) => pid,
        Err(_) => return Ok(()),
    };
    if pid == 0 {
        return Ok(());
    }

    let output = silent_command("taskkill")
        .args(["/F", "/PID", &pid.to_string(), "/T"])
        .output()
        .map_err(|e| format!("Failed to execute taskkill: {e}"))?;

    if output.status.success() {
        std::thread::sleep(Duration::from_millis(800));
        return Ok(());
    }

    let stderr = String::from_utf8_lossy(&output.stderr).to_lowercase();
    if stderr.contains("not found")
        || stderr.contains("no running instance")
        || stderr.contains("not running")
    {
        Ok(())
    } else {
        Err(format!(
            "Failed to kill Steam process: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ))
    }
}

fn kill_steam_by_name() -> Result<(), String> {
    for process in ["steam.exe", "steamwebhelper.exe"] {
        let output = silent_command("taskkill")
            .args(["/F", "/IM", process, "/T"])
            .output()
            .map_err(|e| format!("Failed to execute taskkill: {e}"))?;
        if output.status.success() {
            std::thread::sleep(Duration::from_millis(500));
        }
    }
    Ok(())
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
