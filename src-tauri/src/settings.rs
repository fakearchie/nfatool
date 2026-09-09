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
    /// Optional Steam Web API key. Unlocks level and ban flags; without it we
    /// fall back to the public profile XML for online status only.
    #[serde(default)]
    pub steam_api_key: String,
    /// Exclude the window from screen capture (OBS, Discord, Snipping Tool).
    ///
    /// Off by default, matching `streamer_mode`: the app should not assume you are
    /// streaming, and turning it on silently would make the window screenshot as a
    /// black rectangle — which reads as a bug, not a feature, to anyone who did not
    /// ask for it.
    #[serde(default)]
    pub hide_from_capture: bool,

    // ---- Counter-Strike 2 ----
    /// Launch options written to every account on sign-in. Empty leaves whatever
    /// the account already has, which is why this is a string and not an Option.
    #[serde(default)]
    pub cs2_launch_options: String,
    /// SteamID64 whose CS2 settings tree is copied onto the account being signed
    /// in. Empty disables the copy.
    #[serde(default)]
    pub cs2_config_source: String,
    /// Mark subscribed Workshop items `disabled_locally` so signing in doesn't
    /// re-download them.
    #[serde(default)]
    pub suppress_workshop_downloads: bool,
    /// Turn Steam Remote Play off for the account being signed in.
    #[serde(default)]
    pub disable_remote_play: bool,
    /// Launch CS2 straight after Steam starts.
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
