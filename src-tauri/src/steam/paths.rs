use std::path::{Path, PathBuf};

use winreg::RegKey;

const STEAMID64_BASE: u64 = 76561197960265728;

pub(crate) fn get_steam_path() -> Result<String, String> {
    let hkcu = RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let steam_key = hkcu.open_subkey("SOFTWARE\\Valve\\Steam").map_err(|_| {
        "Steam is not installed or its registry path could not be read.".to_string()
    })?;
    steam_key
        .get_value("SteamPath")
        .map_err(|_| "SteamPath was not found in the Steam registry key.".to_string())
}

pub(crate) fn local_steam_cache_path() -> Result<PathBuf, String> {
    let local_app_data = std::env::var("LOCALAPPDATA")
        .map_err(|_| "LOCALAPPDATA is not set, so the Steam cache path cannot be found.")?;
    Ok(Path::new(&local_app_data).join("Steam"))
}

pub(crate) fn steamid64_to_steamid3(steamid64: &str) -> Result<String, String> {
    let id64: u64 = steamid64.parse().map_err(|_| "Invalid SteamID64")?;
    if id64 < STEAMID64_BASE {
        return Err("SteamID64 too small".into());
    }
    Ok((id64 - STEAMID64_BASE).to_string())
}

pub(crate) fn localconfig_path(steam_path: &Path, steamid3: &str) -> PathBuf {
    steam_path
        .join("userdata")
        .join(steamid3)
        .join("config")
        .join("localconfig.vdf")
}
