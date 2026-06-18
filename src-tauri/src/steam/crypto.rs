use windows::Win32::Security::Cryptography::{CryptProtectData, CRYPT_INTEGER_BLOB};

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

        let encrypted_slice = std::slice::from_raw_parts(data_out.pbData, data_out.cbData as usize);
        let hex_string = encrypted_slice
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>();

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn LocalFree(hmem: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
        }
        LocalFree(data_out.pbData as *mut std::ffi::c_void);

        Ok(hex_string)
    }
}
