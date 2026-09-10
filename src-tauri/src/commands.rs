use std::collections::HashMap;

use base64::Engine;
use serde::Serialize;
use tauri::{AppHandle, Emitter};

use crate::steam::SteamAccount;
use crate::{cache, capture, log, metadata, settings, steam, tray, update, vault};

#[derive(Serialize)]
pub struct AccountDto {
    steamid: String,
    account_name: String,
    persona_name: String,
    display_name: String,
    initials: String,
    most_recent: bool,
    avatar: Option<String>,
    avatar_url: Option<String>,
    has_token: bool,
    token_expires: Option<i64>,
}

fn account_dto(a: &SteamAccount, allow_remote: bool) -> AccountDto {
    let avatar = a.avatar_path.as_ref().and_then(|p| avatar_data_url(p));
    let avatar_url = match (&avatar, allow_remote, a.avatar_hash.as_deref()) {
        (None, true, Some(hash)) => Some(steam::avatar_cdn_url(hash)),
        _ => None,
    };

    AccountDto {
        steamid: a.steamid.clone(),
        account_name: a.account_name.clone(),
        persona_name: a.persona_name.clone(),
        display_name: a.display_name().to_string(),
        initials: a.initials(),
        most_recent: a.most_recent,
        avatar,
        avatar_url,
        has_token: a.token.is_some(),
        token_expires: a.token.as_deref().and_then(steam::token_expiry),
    }
}

static AVATAR_MEMO: cache::FileMemo = cache::FileMemo::new();

fn avatar_data_url(path: &std::path::Path) -> Option<String> {
    AVATAR_MEMO.get_or(path, || encode_avatar(path))
}

fn encode_avatar(path: &std::path::Path) -> Option<String> {
    let bytes = std::fs::read(path).ok()?;
    let mime = match path.extension().and_then(|e| e.to_str()) {
        Some(ext) if ext.eq_ignore_ascii_case("jpg") || ext.eq_ignore_ascii_case("jpeg") => {
            "image/jpeg"
        }
        _ => "image/png",
    };
    let encoded = base64::engine::general_purpose::STANDARD.encode(bytes);
    Some(format!("data:{mime};base64,{encoded}"))
}

const CANCELLED: &str = "__cancelled__";

fn pick_file(app: &AppHandle, save: bool, default_name: Option<&str>) -> Option<std::path::PathBuf> {
    use tauri_plugin_dialog::DialogExt;
    let mut builder = app.dialog().file().add_filter("Text file", &["txt"]);
    if let Some(name) = default_name {
        builder = builder.set_file_name(name);
    }
    let picked = if save {
        builder.blocking_save_file()
    } else {
        builder.blocking_pick_file()
    };
    picked.and_then(|p| p.into_path().ok())
}

fn find_account(steamid: &str) -> Result<SteamAccount, String> {
    steam::load_steam_accounts()?
        .into_iter()
        .find(|a| a.steamid == steamid)
        .ok_or_else(|| "Account not found.".to_string())
}

fn finish_account_op(app: &AppHandle, result: Result<String, String>) -> Result<String, String> {
    if result.is_ok() {
        let _ = tray::rebuild(app);
        let _ = app.emit("accounts-changed", ());
    }
    result
}

#[tauri::command]
pub fn list_accounts() -> Result<Vec<AccountDto>, String> {
    let allow_remote = settings::load_settings().fetch_missing_avatars;
    Ok(steam::load_steam_accounts()?
        .iter()
        .map(|a| account_dto(a, allow_remote))
        .collect())
}

#[tauri::command]
pub fn fetch_account_intel(steamids: Vec<String>) -> Result<HashMap<String, steam::AccountIntel>, String> {
    steam::fetch_account_intel(&steamids)
}

#[tauri::command]
pub fn validate_api_key(key: String) -> Result<bool, String> {
    steam::validate_api_key(&key)
}

#[tauri::command]
pub fn read_clipboard() -> Result<String, String> {
    steam::read_clipboard()
}

#[tauri::command]
pub fn import_clipboard(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::import_from_clipboard())
}

#[tauri::command]
pub fn import_account(app: AppHandle, payload: String) -> Result<String, String> {
    finish_account_op(&app, steam::handle_batch_import(&payload))
}

#[tauri::command]
pub fn import_from_file(app: AppHandle) -> Result<String, String> {
    let Some(path) = pick_file(&app, false, None) else {
        return Err(CANCELLED.to_string());
    };
    let payload = std::fs::read_to_string(&path).map_err(|e| format!("Could not read file: {e}"))?;
    finish_account_op(&app, steam::handle_batch_import(&payload))
}

#[tauri::command]
pub fn import_from_steam_cache(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::import_from_steam_cache())
}

#[tauri::command]
pub fn export_tokens(steamids: Vec<String>) -> Result<String, String> {
    let payload = steam::export_tokens(&steamids)?;
    let n = payload.lines().count();
    steam::write_clipboard(&payload)?;
    Ok(format!("Copied {n} login code{}.", if n == 1 { "" } else { "s" }))
}

#[tauri::command]
pub fn export_tokens_to_file(app: AppHandle, steamids: Vec<String>) -> Result<String, String> {
    let payload = steam::export_tokens(&steamids)?;
    let Some(path) = pick_file(&app, true, Some("nfa-accounts.txt")) else {
        return Err(CANCELLED.to_string());
    };
    std::fs::write(&path, &payload).map_err(|e| format!("Could not write file: {e}"))?;
    let n = payload.lines().count();
    Ok(format!("Saved {n} token{}.", if n == 1 { "" } else { "s" }))
}

#[tauri::command]
pub fn copy_token(steamid: String) -> Result<String, String> {
    steam::write_clipboard(&steam::token_for(&steamid)?)?;
    Ok("Login code copied.".to_string())
}

#[tauri::command]
pub fn copy_text(text: String) -> Result<(), String> {
    steam::write_clipboard(&text)
}

#[tauri::command]
pub fn open_profile(steamid: String) -> Result<(), String> {
    if steamid.is_empty() || !steamid.chars().all(|c| c.is_ascii_digit()) {
        return Err("Not a valid SteamID.".to_string());
    }
    std::process::Command::new("rundll32")
        .arg("url.dll,FileProtocolHandler")
        .arg(format!("https://steamcommunity.com/profiles/{steamid}"))
        .spawn()
        .map_err(|e| format!("Could not open the browser: {e}"))?;
    Ok(())
}

#[tauri::command]
pub fn prune_expired(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::prune_expired_tokens())
}

#[tauri::command]
pub fn check_for_update() -> Result<update::UpdateInfo, String> {
    update::check(env!("CARGO_PKG_VERSION"))
}

#[tauri::command]
pub fn app_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

#[tauri::command]
pub fn get_log() -> Vec<String> {
    log::entries()
}

#[tauri::command]
pub fn clear_log() {
    log::clear();
}

#[tauri::command]
pub fn open_url(url: String) -> Result<(), String> {
    if !url.starts_with("https://github.com/fakearchie/nfatool/") {
        return Err("Refusing to open an unexpected URL.".to_string());
    }
    std::process::Command::new("rundll32")
        .arg("url.dll,FileProtocolHandler")
        .arg(&url)
        .spawn()
        .map_err(|e| format!("Could not open the browser: {e}"))?;
    Ok(())
}

#[tauri::command]
pub fn sign_in(app: AppHandle, steamid: String) -> Result<String, String> {
    let result = finish_account_op(&app, steam::handle_login_account(&find_account(&steamid)?));
    if result.is_ok() {
        metadata::touch_last_used(&steamid);
    }
    result
}

#[tauri::command]
pub fn remove_account(app: AppHandle, steamid: String) -> Result<String, String> {
    let result = finish_account_op(&app, steam::handle_delete_account(&find_account(&steamid)?));
    if result.is_ok() {
        metadata::forget(&steamid);
    }
    result
}

#[tauri::command]
pub fn get_metadata() -> metadata::MetaMap {
    metadata::load()
}

#[tauri::command]
pub fn set_account_color(app: AppHandle, steamid: String, color: String) -> Result<(), String> {
    metadata::set_color(&steamid, &color)?;
    let _ = app.emit("metadata-changed", ());
    Ok(())
}

#[tauri::command]
pub fn set_account_cooldown(
    app: AppHandle,
    steamid: String,
    until: Option<i64>,
) -> Result<(), String> {
    metadata::set_cooldown(&steamid, until)?;
    let _ = app.emit("metadata-changed", ());
    Ok(())
}

#[tauri::command]
pub fn clear_steam(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::handle_clear_steam())
}

#[tauri::command]
pub fn get_settings() -> settings::AppSettings {
    settings::load_settings()
}

#[tauri::command]
pub fn save_settings(app: AppHandle, settings: settings::AppSettings) -> Result<(), String> {
    settings::save_settings(&settings)?;
    capture::apply_to_main(&app, settings.hide_from_capture);
    let _ = tray::rebuild(&app);
    let _ = app.emit("settings-changed", ());
    Ok(())
}

#[tauri::command]
pub fn vault_status() -> vault::VaultStatus {
    vault::status()
}

#[tauri::command]
pub fn vault_unlock(secret: String, is_recovery: bool) -> Result<(), String> {
    vault::unlock(&secret, is_recovery)
}

#[tauri::command]
pub fn vault_lock() {
    vault::lock();
}

#[tauri::command]
pub fn vault_enable(password: String) -> Result<String, String> {
    vault::enable(&password)
}

#[tauri::command]
pub fn vault_disable(password: String) -> Result<(), String> {
    vault::disable(&password)
}

#[tauri::command]
pub fn vault_change(old: String, new: String) -> Result<String, String> {
    vault::change(&old, &new)
}

/// Waits for Steam to actually finish signing in, and reports what happened.
///
/// Writing the login files always "succeeds"; whether Steam accepts the code is
/// only knowable afterwards. A revoked code leaves Steam sitting on its own login
/// window, which looks identical to a slow start until this says otherwise.
#[tauri::command]
pub fn verify_sign_in(steamid: String) -> String {
    match steam::wait_for_sign_in(&steamid, 45) {
        steam::SignInCheck::Confirmed => "ok",
        steam::SignInCheck::OtherAccount => "other",
        steam::SignInCheck::NotSignedIn => "rejected",
    }
    .to_string()
}
