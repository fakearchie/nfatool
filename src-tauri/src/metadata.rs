
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

pub const COLORS: [&str; 6] = ["red", "amber", "green", "blue", "purple", "gray"];

#[derive(Clone, Default, Debug, PartialEq, Serialize, Deserialize)]
pub struct AccountMeta {
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub color: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cooldown_until: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_used: Option<i64>,
}

impl AccountMeta {
    pub fn on_cooldown(&self, now: i64) -> bool {
        self.cooldown_until.is_some_and(|until| until > now)
    }

    fn is_empty(&self) -> bool {
        *self == AccountMeta::default()
    }
}

pub fn format_remaining(seconds: i64) -> String {
    let seconds = seconds.max(0);
    if seconds < 60 {
        return "<1m".to_string();
    }
    let minutes = seconds / 60;
    if minutes < 60 {
        return format!("{minutes}m");
    }
    let hours = minutes / 60;
    if hours < 24 {
        return format!("{hours}h");
    }
    format!("{}d", hours / 24)
}

pub fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn store_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("metadata.json")
}

pub type MetaMap = BTreeMap<String, AccountMeta>;

pub fn load() -> MetaMap {
    let Ok(raw) = fs::read_to_string(store_path()) else {
        return MetaMap::new();
    };
    serde_json::from_str(&raw).unwrap_or_default()
}

fn write(map: &MetaMap) -> Result<(), String> {
    let path = store_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create data dir: {e}"))?;
    }
    let json =
        serde_json::to_string_pretty(map).map_err(|e| format!("Failed to encode metadata: {e}"))?;
    fs::write(&path, json).map_err(|e| format!("Failed to write metadata: {e}"))
}

fn update<F: FnOnce(&mut AccountMeta)>(steamid: &str, edit: F) -> Result<(), String> {
    let mut map = load();
    let entry = map.entry(steamid.to_string()).or_default();
    edit(entry);
    if entry.is_empty() {
        map.remove(steamid);
    }
    write(&map)
}

pub fn set_color(steamid: &str, color: &str) -> Result<(), String> {
    if !color.is_empty() && !COLORS.contains(&color) {
        return Err(format!("Unknown colour: {color}"));
    }
    update(steamid, |m| m.color = color.to_string())
}

pub fn set_cooldown(steamid: &str, until: Option<i64>) -> Result<(), String> {
    let until = until.filter(|u| *u > now_unix());
    update(steamid, |m| m.cooldown_until = until)
}

pub fn touch_last_used(steamid: &str) {
    let _ = update(steamid, |m| m.last_used = Some(now_unix()));
}

pub fn forget(steamid: &str) {
    let mut map = load();
    if map.remove(steamid).is_some() {
        let _ = write(&map);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cooldown_in_the_past_is_not_active() {
        let m = AccountMeta {
            cooldown_until: Some(1_000),
            ..Default::default()
        };
        assert!(!m.on_cooldown(2_000));
        assert!(m.on_cooldown(500));
    }

    #[test]
    fn remaining_time_reads_naturally_at_each_scale() {
        assert_eq!(format_remaining(30), "<1m");
        assert_eq!(format_remaining(60), "1m");
        assert_eq!(format_remaining(59 * 60), "59m");
        assert_eq!(format_remaining(60 * 60), "1h");
        assert_eq!(format_remaining(23 * 3600), "23h");
        assert_eq!(format_remaining(24 * 3600), "1d");
        assert_eq!(format_remaining(181 * 24 * 3600), "181d");
        assert_eq!(format_remaining(-500), "<1m");
    }

    #[test]
    fn no_cooldown_is_not_active() {
        assert!(!AccountMeta::default().on_cooldown(now_unix()));
    }

    #[test]
    fn default_entries_serialize_to_nothing() {
        let json = serde_json::to_string(&AccountMeta::default()).unwrap();
        assert_eq!(json, "{}");
    }

    #[test]
    fn round_trips_a_full_entry() {
        let m = AccountMeta {
            color: "blue".into(),
            cooldown_until: Some(1_700_000_000),
            last_used: Some(1_699_000_000),
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(serde_json::from_str::<AccountMeta>(&json).unwrap(), m);
    }

    #[test]
    fn unknown_colours_are_rejected() {
        assert!(!COLORS.contains(&"chartreuse"));
        assert!(COLORS.contains(&"amber"));
    }

    #[test]
    fn an_emptied_entry_is_considered_empty() {
        let mut m = AccountMeta {
            color: "red".into(),
            ..Default::default()
        };
        assert!(!m.is_empty());
        m.color = String::new();
        assert!(m.is_empty());
    }
}
