// Persistent, app-owned account records (keyed by SteamID64).
//
// Steam's own config files (loginusers.vdf / local.vdf) are volatile — a cache
// reset or a Steam update can wipe the encrypted ConnectCache token, after which
// an account can no longer be signed in without re-importing. Keeping the raw JWT
// here lets every sign-in re-provision Steam from scratch (idempotent + recoverable),
// mirroring the reference tool's token database.

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

#[derive(Clone, Default, Serialize, Deserialize)]
pub(crate) struct AccountRecord {
    pub account_name: String,
    #[serde(default)]
    pub persona_name: String,
    pub token: String,
}

fn store_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("accounts.json")
}

pub(crate) fn load_records() -> BTreeMap<String, AccountRecord> {
    let Ok(raw) = fs::read_to_string(store_path()) else {
        return BTreeMap::new();
    };
    serde_json::from_str(&raw).unwrap_or_default()
}

fn write_records(records: &BTreeMap<String, AccountRecord>) -> Result<(), String> {
    let path = store_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create data dir: {e}"))?;
    }
    let json = serde_json::to_string_pretty(records)
        .map_err(|e| format!("Failed to encode accounts store: {e}"))?;
    fs::write(&path, json).map_err(|e| format!("Failed to write accounts store: {e}"))
}

// Best-effort upsert — a failure to persist must never block the actual login.
pub(crate) fn save_record(steamid: &str, account_name: &str, persona_name: &str, token: &str) {
    let mut records = load_records();
    records.insert(
        steamid.to_string(),
        AccountRecord {
            account_name: account_name.to_string(),
            persona_name: persona_name.to_string(),
            token: token.to_string(),
        },
    );
    let _ = write_records(&records);
}

pub(crate) fn remove_record(steamid: &str) {
    let mut records = load_records();
    if records.remove(steamid).is_some() {
        let _ = write_records(&records);
    }
}
