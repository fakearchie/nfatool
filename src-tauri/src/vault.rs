use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;

use aes_gcm::aead::{Aead, KeyInit, Payload};
use aes_gcm::{Aes256Gcm, Key, Nonce};
use argon2::{Algorithm, Argon2, Params, Version};
use base64::Engine;
use rand::RngCore;
use serde::{Deserialize, Serialize};
use zeroize::Zeroize;

const B64: base64::engine::general_purpose::GeneralPurpose =
    base64::engine::general_purpose::STANDARD;

// Marks a token encrypted by this vault. The DPAPI format is bare hex and can
// never contain ':', so the two are always distinguishable.
const PREFIX: &str = "v1:";

const M_COST: u32 = 65_536;
const T_COST: u32 = 3;
const P_COST: u32 = 1;

#[derive(Serialize, Deserialize)]
struct Wrapped {
    salt: String,
    nonce: String,
    ct: String,
}

#[derive(Serialize, Deserialize)]
struct VaultFile {
    version: u32,
    m_cost: u32,
    t_cost: u32,
    p_cost: u32,
    password: Wrapped,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    recovery: Option<Wrapped>,
}

static UNLOCKED: Mutex<Option<[u8; 32]>> = Mutex::new(None);

#[derive(Serialize)]
pub struct VaultStatus {
    pub enabled: bool,
    pub unlocked: bool,
}

fn vault_path() -> PathBuf {
    let base = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base)
        .join("shop.archievable.desktop")
        .join("vault.json")
}

fn read_vault() -> Option<VaultFile> {
    serde_json::from_str(&fs::read_to_string(vault_path()).ok()?).ok()
}

pub fn is_enabled() -> bool {
    vault_path().exists()
}

pub fn status() -> VaultStatus {
    VaultStatus {
        enabled: is_enabled(),
        unlocked: UNLOCKED.lock().map(|k| k.is_some()).unwrap_or(false),
    }
}

fn random(n: usize) -> Vec<u8> {
    let mut buf = vec![0u8; n];
    rand::thread_rng().fill_bytes(&mut buf);
    buf
}

fn derive(secret: &[u8], salt: &[u8], m: u32, t: u32, p: u32) -> Result<[u8; 32], String> {
    let params = Params::new(m, t, p, Some(32)).map_err(|e| format!("Bad KDF params: {e}"))?;
    let argon = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    let mut key = [0u8; 32];
    argon
        .hash_password_into(secret, salt, &mut key)
        .map_err(|_| "Could not derive the key.".to_string())?;
    Ok(key)
}

fn seal(key: &[u8; 32], plaintext: &[u8], aad: &[u8]) -> Result<(String, String), String> {
    let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(key));
    let nonce_bytes = random(12);
    let ct = cipher
        .encrypt(
            Nonce::from_slice(&nonce_bytes),
            Payload {
                msg: plaintext,
                aad,
            },
        )
        .map_err(|_| "Encryption failed.".to_string())?;
    Ok((B64.encode(&nonce_bytes), B64.encode(ct)))
}

fn open(key: &[u8; 32], nonce: &str, ct: &str, aad: &[u8]) -> Result<Vec<u8>, String> {
    let nonce_bytes = B64.decode(nonce).map_err(|_| "Corrupt nonce.".to_string())?;
    let ct_bytes = B64.decode(ct).map_err(|_| "Corrupt ciphertext.".to_string())?;
    Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(key))
        .decrypt(
            Nonce::from_slice(&nonce_bytes),
            Payload {
                msg: &ct_bytes,
                aad,
            },
        )
        .map_err(|_| "Wrong password.".to_string())
}

fn wrap_dek(secret: &[u8], dek: &[u8; 32]) -> Result<Wrapped, String> {
    let salt = random(16);
    let mut kek = derive(secret, &salt, M_COST, T_COST, P_COST)?;
    let sealed = seal(&kek, dek, b"dek");
    kek.zeroize();
    let (nonce, ct) = sealed?;
    Ok(Wrapped {
        salt: B64.encode(&salt),
        nonce,
        ct,
    })
}

fn unwrap_dek(secret: &[u8], w: &Wrapped, m: u32, t: u32, p: u32) -> Result<[u8; 32], String> {
    let salt = B64.decode(&w.salt).map_err(|_| "Corrupt salt.".to_string())?;
    let mut kek = derive(secret, &salt, m, t, p)?;
    let opened = open(&kek, &w.nonce, &w.ct, b"dek");
    kek.zeroize();

    let mut bytes = opened?;
    if bytes.len() != 32 {
        bytes.zeroize();
        return Err("Corrupt vault key.".to_string());
    }
    let mut dek = [0u8; 32];
    dek.copy_from_slice(&bytes);
    bytes.zeroize();
    Ok(dek)
}

// Crockford base32: no I, L, O or U, so a handwritten code cannot be misread.
const ALPHABET: &[u8] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

fn to_base32(bytes: &[u8]) -> String {
    let mut chars = String::new();
    let (mut acc, mut bits) = (0u32, 0u32);
    for &b in bytes {
        acc = (acc << 8) | b as u32;
        bits += 8;
        while bits >= 5 {
            bits -= 5;
            chars.push(ALPHABET[((acc >> bits) & 31) as usize] as char);
        }
    }
    if bits > 0 {
        chars.push(ALPHABET[((acc << (5 - bits)) & 31) as usize] as char);
    }
    chars
        .as_bytes()
        .chunks(5)
        .filter_map(|c| std::str::from_utf8(c).ok())
        .collect::<Vec<_>>()
        .join("-")
}

fn normalize_recovery(code: &str) -> Vec<u8> {
    code.to_ascii_uppercase()
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .map(|c| match c {
            'I' | 'L' => '1',
            'O' => '0',
            'U' => 'V',
            other => other,
        })
        .collect::<String>()
        .into_bytes()
}

pub fn unlock(secret: &str, is_recovery: bool) -> Result<(), String> {
    let vault = read_vault().ok_or("No password is set.")?;
    let wrapped = if is_recovery {
        vault.recovery.as_ref().ok_or("No recovery code is set.")?
    } else {
        &vault.password
    };
    let material = if is_recovery {
        normalize_recovery(secret)
    } else {
        secret.as_bytes().to_vec()
    };

    let dek = unwrap_dek(&material, wrapped, vault.m_cost, vault.t_cost, vault.p_cost).map_err(
        |_| {
            if is_recovery {
                "That recovery code is not right."
            } else {
                "Wrong password."
            }
        },
    )?;

    *UNLOCKED.lock().map_err(|_| "Vault is in a bad state.")? = Some(dek);
    Ok(())
}

pub fn lock() {
    if let Ok(mut key) = UNLOCKED.lock() {
        if let Some(mut dek) = key.take() {
            dek.zeroize();
        }
    }
}

fn dek() -> Result<[u8; 32], String> {
    UNLOCKED
        .lock()
        .map_err(|_| "Vault is in a bad state.".to_string())?
        .ok_or_else(|| "Unlock the app first.".to_string())
}

pub fn encrypt_token(steamid: &str, token: &str) -> Result<String, String> {
    if !is_enabled() {
        return crate::steam::dpapi_protect_token(token, steamid);
    }
    let key = dek()?;
    let (nonce, ct) = seal(&key, token.as_bytes(), steamid.as_bytes())?;
    Ok(format!("{PREFIX}{nonce}:{ct}"))
}

pub fn decrypt_token(steamid: &str, stored: &str) -> Result<String, String> {
    let Some(rest) = stored.strip_prefix(PREFIX) else {
        return crate::steam::dpapi_unprotect_token(stored, steamid);
    };
    let (nonce, ct) = rest.split_once(':').ok_or("Corrupt token record.")?;
    let key = dek()?;
    let bytes = open(&key, nonce, ct, steamid.as_bytes())?;
    String::from_utf8(bytes).map_err(|_| "Corrupt token record.".to_string())
}

/// Turns the password on and returns the recovery code, which is shown once.
pub fn enable(password: &str) -> Result<String, String> {
    if password.chars().count() < 4 {
        return Err("Use at least 4 characters.".to_string());
    }
    if is_enabled() {
        return Err("A password is already set.".to_string());
    }

    let plain = crate::steam::all_tokens_plaintext()?;

    let mut dek = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut dek);
    let recovery_code = to_base32(&random(20));

    let vault = VaultFile {
        version: 1,
        m_cost: M_COST,
        t_cost: T_COST,
        p_cost: P_COST,
        password: wrap_dek(password.as_bytes(), &dek)?,
        recovery: Some(wrap_dek(&normalize_recovery(&recovery_code), &dek)?),
    };

    write_vault(&vault)?;
    *UNLOCKED.lock().map_err(|_| "Vault is in a bad state.")? = Some(dek);

    // Roll the vault back if re-encryption fails, so tokens are never left sealed
    // under a key that is no longer stored anywhere.
    if let Err(e) = crate::steam::rewrite_tokens(&plain) {
        let _ = fs::remove_file(vault_path());
        lock();
        return Err(e);
    }
    Ok(recovery_code)
}

pub fn disable(password: &str) -> Result<(), String> {
    unlock(password, false)?;
    let plain = crate::steam::all_tokens_plaintext()?;
    fs::remove_file(vault_path()).map_err(|e| format!("Could not remove the vault: {e}"))?;
    lock();
    crate::steam::rewrite_tokens(&plain)
}

pub fn change(old: &str, new: &str) -> Result<String, String> {
    disable(old)?;
    enable(new)
}

fn write_vault(vault: &VaultFile) -> Result<(), String> {
    let path = vault_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Failed to create data dir: {e}"))?;
    }
    let json =
        serde_json::to_string_pretty(vault).map_err(|e| format!("Failed to encode vault: {e}"))?;
    fs::write(&path, json).map_err(|e| format!("Failed to write vault: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seal_and_open_round_trip() {
        let key = [7u8; 32];
        let (nonce, ct) = seal(&key, b"secret", b"7656").unwrap();
        assert_eq!(open(&key, &nonce, &ct, b"7656").unwrap(), b"secret");
    }

    #[test]
    fn a_blob_cannot_be_moved_between_accounts() {
        let key = [7u8; 32];
        let (nonce, ct) = seal(&key, b"secret", b"account-a").unwrap();
        assert!(open(&key, &nonce, &ct, b"account-b").is_err());
    }

    #[test]
    fn the_wrong_key_fails_rather_than_returning_junk() {
        let (nonce, ct) = seal(&[1u8; 32], b"secret", b"x").unwrap();
        assert!(open(&[2u8; 32], &nonce, &ct, b"x").is_err());
    }

    #[test]
    fn wrapping_round_trips_and_rejects_a_wrong_password() {
        let dek = [42u8; 32];
        let w = wrap_dek(b"hunter2", &dek).unwrap();
        assert_eq!(unwrap_dek(b"hunter2", &w, M_COST, T_COST, P_COST).unwrap(), dek);
        assert!(unwrap_dek(b"hunter3", &w, M_COST, T_COST, P_COST).is_err());
    }

    #[test]
    fn recovery_codes_avoid_look_alike_characters() {
        let code = to_base32(&[0xAB; 20]);
        assert_eq!(code.len(), 32 + 6, "32 chars in 5-char groups");
        assert!(!code.contains('I') && !code.contains('L') && !code.contains('U'));
    }

    #[test]
    fn a_recovery_code_is_forgiving_about_how_it_is_typed() {
        assert_eq!(normalize_recovery("ilo-u"), normalize_recovery("110-V"));
        assert_eq!(normalize_recovery("ab cd"), normalize_recovery("ABCD"));
    }

    #[test]
    fn a_vault_token_is_distinguishable_from_a_dpapi_one() {
        assert!("v1:abc:def".starts_with(PREFIX));
        assert!(!"deadbeef0123".starts_with(PREFIX));
    }
}
