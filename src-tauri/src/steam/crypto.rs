use windows::Win32::Security::Cryptography::{
    CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB,
};

/// Both `Crypt*Data` calls hand back a `LocalAlloc`'d buffer the caller must free.
///
/// # Safety
/// `p` must be a pointer DPAPI wrote into a `CRYPT_INTEGER_BLOB`, freed once.
unsafe fn local_free(p: *mut u8) {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn LocalFree(hmem: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
    }
    LocalFree(p as *mut std::ffi::c_void);
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn from_hex(hex: &str) -> Option<Vec<u8>> {
    if hex.len() % 2 != 0 {
        return None;
    }
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).ok())
        .collect()
}

// Namespaced so a blob from our store can never be mistaken for Steam's own
// ConnectCache format, which uses different entropy and a description blob.
const ENTROPY_PREFIX: &str = "nfa.pub tool/token/v1/";

/// DPAPI-protects a token for the current Windows user on this machine, so a
/// copied `accounts.json` is inert on another PC or under another user account.
///
/// `tag` (the SteamID) is folded into the entropy, which additionally pins each
/// blob to its own record — swapping two `token_enc` values makes both fail.
pub(crate) fn dpapi_protect(plain: &str, tag: &str) -> Result<String, String> {
    let entropy_string = format!("{ENTROPY_PREFIX}{tag}");
    let data = plain.as_bytes();
    let entropy_bytes = entropy_string.as_bytes();

    let data_in = CRYPT_INTEGER_BLOB {
        cbData: data.len() as u32,
        pbData: data.as_ptr() as *mut u8,
    };
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy_bytes.len() as u32,
        pbData: entropy_bytes.as_ptr() as *mut u8,
    };
    let mut out = CRYPT_INTEGER_BLOB::default();

    unsafe {
        CryptProtectData(
            &data_in,
            windows::core::PCWSTR::null(),
            Some(&entropy),
            None,
            None,
            0,
            &mut out,
        )
        .map_err(|e| format!("CryptProtectData failed: {e}"))?;

        let hex = to_hex(std::slice::from_raw_parts(out.pbData, out.cbData as usize));
        local_free(out.pbData);
        Ok(hex)
    }
}

/// Reverses [`dpapi_protect`]. Fails (rather than returning junk) when the blob
/// was written by a different Windows user, on a different machine, or for a
/// different SteamID.
pub(crate) fn dpapi_unprotect(hex: &str, tag: &str) -> Result<String, String> {
    let blob = from_hex(hex).ok_or_else(|| "Token blob is not valid hex.".to_string())?;
    let entropy_string = format!("{ENTROPY_PREFIX}{tag}");
    let entropy_bytes = entropy_string.as_bytes();

    let data_in = CRYPT_INTEGER_BLOB {
        cbData: blob.len() as u32,
        pbData: blob.as_ptr() as *mut u8,
    };
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy_bytes.len() as u32,
        pbData: entropy_bytes.as_ptr() as *mut u8,
    };
    let mut out = CRYPT_INTEGER_BLOB::default();

    unsafe {
        CryptUnprotectData(
            &data_in,
            None,
            Some(&entropy),
            None,
            None,
            0,
            &mut out,
        )
        .map_err(|e| format!("CryptUnprotectData failed: {e}"))?;

        let plain = std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec();
        local_free(out.pbData);
        String::from_utf8(plain).map_err(|_| "Decrypted token is not UTF-8.".to_string())
    }
}

// Steam formats the CRC32 key as hex with leading zeros stripped and a trailing "1".
pub(crate) fn compute_crc32(data: &str) -> String {
    let crc32_value = crc32fast::hash(data.as_bytes());
    let hex = format!("{crc32_value:08x}");
    let trimmed = hex.trim_start_matches('0');
    if trimmed.is_empty() {
        "01".to_string()
    } else {
        format!("{trimmed}1")
    }
}

// DPAPI (CryptProtectData) with the account name as entropy and Steam's "BObfuscateBuffer" description blob.
pub(crate) fn steam_encrypt(token: &str, account_name: &str) -> Result<String, String> {
    let data_to_encrypt = token.as_bytes();
    let byte_string =
        b"B\x00O\x00b\x00f\x00u\x00s\x00c\x00a\x00t\x00e\x00B\x00u\x00f\x00f\x00e\x00r\x00\x00\x00";
    let account_name_bytes = account_name.as_bytes();

    let data_in = CRYPT_INTEGER_BLOB {
        cbData: data_to_encrypt.len() as u32,
        pbData: data_to_encrypt.as_ptr() as *mut u8,
    };
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: account_name_bytes.len() as u32,
        pbData: account_name_bytes.as_ptr() as *mut u8,
    };

    let description = String::from_utf8_lossy(byte_string);
    let description_wide: Vec<u16> = description.encode_utf16().chain(Some(0)).collect();
    let description_pcwstr = windows::core::PCWSTR(description_wide.as_ptr());
    let mut data_out = CRYPT_INTEGER_BLOB::default();

    unsafe {
        let success = CryptProtectData(
            &data_in,
            description_pcwstr,
            Some(&entropy),
            None,
            None,
            0x11,
            &mut data_out,
        );
        if success.is_err() {
            return Err("CryptProtectData failed".to_string());
        }

        let hex_string = to_hex(std::slice::from_raw_parts(
            data_out.pbData,
            data_out.cbData as usize,
        ));
        local_free(data_out.pbData);

        Ok(hex_string)
    }
}

/// Reverses [`steam_encrypt`] — reads a token back out of Steam's own ConnectCache.
///
/// Same entropy (the account name) and the `CRYPTPROTECT_UI_FORBIDDEN` flag, since
/// this runs on a background thread with no window to host a prompt. Fails for an
/// account that belongs to a different Windows user, which is expected and not an
/// error worth surfacing — the caller just skips it.
pub(crate) fn steam_decrypt(hex: &str, account_name: &str) -> Result<String, String> {
    let blob = from_hex(hex).ok_or_else(|| "ConnectCache value is not hex.".to_string())?;
    let entropy_bytes = account_name.as_bytes();

    let data_in = CRYPT_INTEGER_BLOB {
        cbData: blob.len() as u32,
        pbData: blob.as_ptr() as *mut u8,
    };
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy_bytes.len() as u32,
        pbData: entropy_bytes.as_ptr() as *mut u8,
    };
    let mut out = CRYPT_INTEGER_BLOB::default();

    unsafe {
        CryptUnprotectData(&data_in, None, Some(&entropy), None, None, 1, &mut out)
            .map_err(|e| format!("CryptUnprotectData failed: {e}"))?;

        let plain = std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec();
        local_free(out.pbData);
        // Steam stores a NUL-terminated string; trim it or the JWT check fails.
        let plain = plain.split(|b| *b == 0).next().unwrap_or(&plain).to_vec();
        String::from_utf8(plain).map_err(|_| "Decrypted value is not UTF-8.".to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn steam_blob_round_trips() {
        // Confirms our decrypt actually reverses the encrypt Steam's cache uses,
        // including the entropy and the NUL trim.
        let token = "eyJhbGciOiJFZERTQSJ9.cGF5bG9hZA.c2ln";
        let blob = steam_encrypt(token, "archie").expect("encrypt");
        assert_eq!(steam_decrypt(&blob, "archie").expect("decrypt"), token);
    }

    #[test]
    fn steam_blob_needs_the_right_account_name() {
        let blob = steam_encrypt("secret", "archie").expect("encrypt");
        assert!(steam_decrypt(&blob, "someone_else").is_err());
    }

    #[test]
    fn hex_round_trips() {
        assert_eq!(from_hex(&to_hex(&[0, 1, 15, 16, 255])), Some(vec![0, 1, 15, 16, 255]));
    }

    #[test]
    fn rejects_malformed_hex() {
        assert_eq!(from_hex("abc"), None); // odd length
        assert_eq!(from_hex("zz"), None); // not hex
    }

    #[test]
    fn dpapi_round_trips() {
        let token = "76561199609128681||eyJhbGciOiJFZERTQSJ9.payload.signature";
        let blob = dpapi_protect(token, "76561199609128681").expect("protect");
        assert_ne!(blob, token, "the stored blob must not contain the plaintext");
        assert!(!blob.contains("eyJ"));
        let back = dpapi_unprotect(&blob, "76561199609128681").expect("unprotect");
        assert_eq!(back, token);
    }

    #[test]
    fn wrong_tag_fails_rather_than_returning_junk() {
        let blob = dpapi_protect("secret", "76561199609128681").expect("protect");
        assert!(dpapi_unprotect(&blob, "76561199609128682").is_err());
    }
}
