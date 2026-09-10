use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AppSettings {
    #[serde(default = "default_true")]
    pub always_invisible: bool,
    #[serde(default)]
    pub cancel_downloads_on_login: bool,
    #[serde(default)]
    pub streamer_mode: bool,
    #[serde(default)]
    pub launch_steam_minimized: bool,
    #[serde(default)]
    pub mute_notifications_on_login: bool,
    #[serde(default = "default_true")]
    pub fetch_missing_avatars: bool,
    #[serde(default)]
    pub steam_api_key: String,
    #[serde(default)]
    pub hide_from_capture: bool,
    #[serde(default)]
    pub auto_remove_rejected: bool,
    #[serde(default = "default_true")]
    pub check_updates_on_start: bool,

    #[serde(default)]
    pub cs2_launch_options: String,
    #[serde(default)]
    pub cs2_config_source: String,
    #[serde(default)]
    pub suppress_workshop_downloads: bool,
    #[serde(default)]
    pub disable_remote_play: bool,
    #[serde(default)]
    pub launch_cs2_on_login: bool,
}

fn default_true() -> bool {
    true
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            always_invisible: true,
            cancel_downloads_on_login: false,
            streamer_mode: false,
            launch_steam_minimized: false,
            mute_notifications_on_login: false,
            fetch_missing_avatars: true,
            steam_api_key: String::new(),
            hide_from_capture: false,
            auto_remove_rejected: false,
            check_updates_on_start: true,
            cs2_launch_options: String::new(),
            cs2_config_source: String::new(),
            suppress_workshop_downloads: false,
            disable_remote_play: false,
            launch_cs2_on_login: false,
        }
    }
}

pub fn settings_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("settings.json")
}

pub fn load_settings() -> AppSettings {
    let path = settings_path();
    let Ok(raw) = fs::read_to_string(&path) else {
        return AppSettings::default();
    };
    serde_json::from_str(&raw).unwrap_or_default()
}

pub fn save_settings(settings: &AppSettings) -> Result<(), String> {
    let path = settings_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create settings dir: {e}"))?;
    }
    let json = serde_json::to_string_pretty(settings)
        .map_err(|e| format!("Failed to encode settings: {e}"))?;
    fs::write(&path, json).map_err(|e| format!("Failed to write settings: {e}"))
}
