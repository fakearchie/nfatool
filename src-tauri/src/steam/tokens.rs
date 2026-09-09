// Persistent, app-owned account records (keyed by SteamID64).
//
// Steam's own config files (loginusers.vdf / local.vdf) are volatile — a cache
// reset or a Steam update can wipe the encrypted ConnectCache token, after which
// an account can no longer be signed in without re-importing. Keeping the raw JWT
// here lets every sign-in re-provision Steam from scratch (idempotent + recoverable),
// mirroring the reference tool's token database.
//
// Tokens are DPAPI-protected on disk (see `crypto::dpapi_protect`): a refresh token
// is a password equivalent, so `accounts.json` must be useless if it is copied off
// the machine. In memory an `AccountRecord` always holds the plaintext JWT — the
// encryption boundary is the file, and nothing above this module sees it.

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use super::crypto;

#[derive(Clone, Default)]
pub(crate) struct AccountRecord {
    pub account_name: String,
    pub persona_name: String,
    /// Plaintext JWT. Empty when the on-disk blob could not be decrypted.
    pub token: String,
}

/// On-disk shape. `token_enc` is what we write; `token` is only ever *read*, to
/// migrate stores written before encryption existed.
#[derive(Default, Serialize, Deserialize)]
struct StoredRecord {
    account_name: String,
    #[serde(default)]
    persona_name: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    token_enc: String,
    /// Legacy plaintext, written by builds before v0.4. `skip_serializing` makes
    /// "read it, never write it" a property of the type rather than something
    /// every writer has to remember.
    #[serde(default, skip_serializing)]
    token: String,
}

fn store_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("accounts.json")
}

fn read_stored() -> BTreeMap<String, StoredRecord> {
    let Ok(raw) = fs::read_to_string(store_path()) else {
        return BTreeMap::new();
    };
    serde_json::from_str(&raw).unwrap_or_default()
}

pub(crate) fn load_records() -> BTreeMap<String, AccountRecord> {
    let stored = read_stored();
    let mut has_plaintext = false;
    let mut records = BTreeMap::new();

    for (steamid, rec) in &stored {
        let token = if !rec.token_enc.is_empty() {
            // A blob that will not open belongs to another user or machine. Keep
            // the account visible (name, avatar, removal) but with no token, which
            // callers already treat as "cannot re-provision".
            crypto::dpapi_unprotect(&rec.token_enc, steamid).unwrap_or_default()
        } else {
            if !rec.token.is_empty() {
                has_plaintext = true;
            }
            rec.token.clone()
        };
        records.insert(
            steamid.clone(),
            AccountRecord {
                account_name: rec.account_name.clone(),
                persona_name: rec.persona_name.clone(),
                token,
            },
        );
    }

    // First run after upgrading: rewrite the store so the plaintext stops existing.
    if has_plaintext {
        let _ = write_records(&records);
    }

    records
}

fn write_records(records: &BTreeMap<String, AccountRecord>) -> Result<(), String> {
    let path = store_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create data dir: {e}"))?;
    }

    let mut stored: BTreeMap<String, StoredRecord> = BTreeMap::new();
    for (steamid, rec) in records {
        // An empty token means we failed to decrypt it above. Re-encrypting the
        // empty string would destroy a blob that is merely unreadable *here* — it
        // may still be the user's only copy on their own machine.
        let token_enc = if rec.token.is_empty() {
            read_stored()
                .get(steamid)
                .map(|s| s.token_enc.clone())
                .unwrap_or_default()
        } else {
            crypto::dpapi_protect(&rec.token, steamid)?
        };
        stored.insert(
            steamid.clone(),
            StoredRecord {
                account_name: rec.account_name.clone(),
                persona_name: rec.persona_name.clone(),
                token_enc,
                token: String::new(),
            },
        );
    }

    let json = serde_json::to_string_pretty(&stored)
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn legacy_plaintext_store_is_readable() {
        let json = r#"{"76561199609128681":{"account_name":"archie","persona_name":"archie","token":"archie||jwt"}}"#;
        let stored: BTreeMap<String, StoredRecord> = serde_json::from_str(json).unwrap();
        let rec = &stored["76561199609128681"];
        assert_eq!(rec.token, "archie||jwt");
        assert!(rec.token_enc.is_empty());
    }

    #[test]
    fn encrypted_store_is_readable() {
        let json = r#"{"7656":{"account_name":"a","persona_name":"b","token_enc":"deadbeef"}}"#;
        let stored: BTreeMap<String, StoredRecord> = serde_json::from_str(json).unwrap();
        assert_eq!(stored["7656"].token_enc, "deadbeef");
        assert!(stored["7656"].token.is_empty());
    }

    /// The whole point of the migration: a re-serialized store must not carry the
    /// legacy plaintext field forward.
    #[test]
    fn plaintext_field_is_never_written_back() {
        let mut stored = BTreeMap::new();
        stored.insert(
            "7656".to_string(),
            StoredRecord {
                account_name: "archie".into(),
                persona_name: "archie".into(),
                token_enc: "abcd".into(),
                token: "archie||jwt".into(),
            },
        );
        let out = serde_json::to_string(&stored).unwrap();
        assert!(out.contains("token_enc"));
        assert!(!out.contains("archie||jwt"));
    }

    #[test]
    fn round_trips_through_dpapi() {
        let mut records = BTreeMap::new();
        records.insert(
            "76561199609128681".to_string(),
            AccountRecord {
                account_name: "archie".into(),
                persona_name: "archie".into(),
                token: "archie||eyJhbGciOiJFZERTQSJ9.payload.sig".into(),
            },
        );

        // Exercise the encrypt/decrypt pair the store relies on, without touching
        // the real %APPDATA% file.
        let enc = crypto::dpapi_protect(&records["76561199609128681"].token, "76561199609128681")
            .expect("protect");
        let dec = crypto::dpapi_unprotect(&enc, "76561199609128681").expect("unprotect");
        assert_eq!(dec, records["76561199609128681"].token);
    }
}
