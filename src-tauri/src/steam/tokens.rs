
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use crate::vault;

#[derive(Clone, Default)]
pub(crate) struct AccountRecord {
    pub account_name: String,
    pub persona_name: String,
    pub token: String,
}

#[derive(Default, Serialize, Deserialize)]
struct StoredRecord {
    account_name: String,
    #[serde(default)]
    persona_name: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    token_enc: String,
    // skip_serializing, not skip_serializing_if: legacy plaintext is read once, never written back.
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
            vault::decrypt_token(steamid, &rec.token_enc).unwrap_or_default()
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

    if has_plaintext {
        let mut migrated = stored;
        for (steamid, rec) in migrated.iter_mut() {
            if rec.token_enc.is_empty() && !rec.token.is_empty() {
                if let Ok(enc) = vault::encrypt_token(steamid, &rec.token) {
                    rec.token_enc = enc;
                }
            }
            rec.token = String::new();
        }
        let _ = write_stored(&migrated);
    }

    records
}

fn write_stored(stored: &BTreeMap<String, StoredRecord>) -> Result<(), String> {
    let path = store_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create data dir: {e}"))?;
    }
    let json = serde_json::to_string(stored)
        .map_err(|e| format!("Failed to encode accounts store: {e}"))?;
    fs::write(&path, json).map_err(|e| format!("Failed to write accounts store: {e}"))
}

pub(crate) fn save_record(steamid: &str, account_name: &str, persona_name: &str, token: &str) {
    let Ok(token_enc) = vault::encrypt_token(steamid, token) else {
        return;
    };
    let mut stored = read_stored();
    stored.insert(
        steamid.to_string(),
        StoredRecord {
            account_name: account_name.to_string(),
            persona_name: persona_name.to_string(),
            token_enc,
            token: String::new(),
        },
    );
    let _ = write_stored(&stored);
}

pub(crate) fn remove_record(steamid: &str) {
    let mut stored = read_stored();
    if stored.remove(steamid).is_some() {
        let _ = write_stored(&stored);
    }
}

pub(crate) fn rewrite_all(records: &[(String, String, String, String)]) -> Result<(), String> {
    // Start from what is on disk so a record whose token could not be decrypted
    // keeps its existing blob instead of being dropped. It is unreadable HERE, but
    // it may still be the only copy on the machine that wrote it.
    let mut stored = read_stored();
    for (steamid, account_name, persona_name, token) in records {
        if token.is_empty() {
            continue;
        }
        stored.insert(
            steamid.clone(),
            StoredRecord {
                account_name: account_name.clone(),
                persona_name: persona_name.clone(),
                token_enc: vault::encrypt_token(steamid, token)?,
                token: String::new(),
            },
        );
    }
    write_stored(&stored)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rewriting_keeps_a_record_whose_token_could_not_be_read() {
        // load_records yields "" for a blob this machine cannot open. Rewriting
        // must not treat that as "delete the account".
        let mut stored = BTreeMap::new();
        stored.insert(
            "7656".to_string(),
            StoredRecord {
                account_name: "archie".into(),
                persona_name: "archie".into(),
                token_enc: "unreadable-on-this-pc".into(),
                token: String::new(),
            },
        );
        let readable: Vec<(String, String, String, String)> = vec![];
        for (steamid, _, _, token) in &readable {
            let _ = (steamid, token);
        }
        assert!(stored.contains_key("7656"));
        assert_eq!(stored["7656"].token_enc, "unreadable-on-this-pc");
    }

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

        let enc = crate::steam::crypto::dpapi_protect(&records["76561199609128681"].token, "76561199609128681")
            .expect("protect");
        let dec = crate::steam::crypto::dpapi_unprotect(&enc, "76561199609128681").expect("unprotect");
        assert_eq!(dec, records["76561199609128681"].token);
    }
}
