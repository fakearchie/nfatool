mod capture;
mod log;
mod metadata;
mod settings;
mod steam;
mod tray;
mod update;
mod window_state;

use std::collections::HashMap;

use base64::Engine;
use serde::Serialize;
use steam::SteamAccount;
use tauri::{AppHandle, Emitter};

#[derive(Serialize)]
struct AccountDto {
    steamid: String,
    account_name: String,
    persona_name: String,
    display_name: String,
    initials: String,
    most_recent: bool,
    avatar: Option<String>,
    /// Steam CDN URL, sent only when there is no locally cached image and the
    /// user has left avatar fetching enabled. The webview loads it directly.
    avatar_url: Option<String>,
    /// Whether a login code is saved for this account at all — without one, a
    /// sign-in falls back to whatever Steam still has cached.
    has_token: bool,
    /// Unix seconds the saved code expires. `None` means no code, or one whose
    /// expiry we could not read — never "expired".
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

fn avatar_data_url(path: &std::path::Path) -> Option<String> {
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

/// Sentinel for "the user closed the picker". The frontend swallows it rather than
/// showing an error toast — dismissing a dialog is not a failure.
const CANCELLED: &str = "__cancelled__";

/// Blocking native file picker. Commands run off the main thread, so blocking here
/// is safe; doing it in Rust keeps the dialog plugin out of the webview entirely.
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
fn list_accounts() -> Result<Vec<AccountDto>, String> {
    let allow_remote = settings::load_settings().fetch_missing_avatars;
    Ok(steam::load_steam_accounts()?
        .iter()
        .map(|a| account_dto(a, allow_remote))
        .collect())
}

/// Public, read-only account data (level, bans, online status). Never touches an
/// account — see steam::webapi.
#[tauri::command]
fn fetch_account_intel(steamids: Vec<String>) -> Result<HashMap<String, steam::AccountIntel>, String> {
    steam::fetch_account_intel(&steamids)
}

#[tauri::command]
fn validate_api_key(key: String) -> Result<bool, String> {
    steam::validate_api_key(&key)
}

#[tauri::command]
fn read_clipboard() -> Result<String, String> {
    steam::read_clipboard()
}

#[tauri::command]
fn import_clipboard(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::import_from_clipboard())
}

#[tauri::command]
fn import_account(app: AppHandle, payload: String) -> Result<String, String> {
    finish_account_op(&app, steam::handle_batch_import(&payload))
}

/// Reads login codes out of a .txt file. The picker runs on the Rust side, so the
/// webview never gets filesystem access of its own.
#[tauri::command]
fn import_from_file(app: AppHandle) -> Result<String, String> {
    let Some(path) = pick_file(&app, false, None) else {
        return Err(CANCELLED.to_string());
    };
    let payload = std::fs::read_to_string(&path).map_err(|e| format!("Could not read file: {e}"))?;
    finish_account_op(&app, steam::handle_batch_import(&payload))
}

/// Harvests tokens for accounts already signed in on this PC — no codes needed.
#[tauri::command]
fn import_from_steam_cache(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::import_from_steam_cache())
}

#[tauri::command]
fn export_tokens(steamids: Vec<String>) -> Result<String, String> {
    let payload = steam::export_tokens(&steamids)?;
    let n = payload.lines().count();
    steam::write_clipboard(&payload)?;
    Ok(format!("Copied {n} login code{}.", if n == 1 { "" } else { "s" }))
}

#[tauri::command]
fn export_tokens_to_file(app: AppHandle, steamids: Vec<String>) -> Result<String, String> {
    let payload = steam::export_tokens(&steamids)?;
    let Some(path) = pick_file(&app, true, Some("nfa-accounts.txt")) else {
        return Err(CANCELLED.to_string());
    };
    std::fs::write(&path, &payload).map_err(|e| format!("Could not write file: {e}"))?;
    let n = payload.lines().count();
    Ok(format!("Saved {n} token{}.", if n == 1 { "" } else { "s" }))
}

#[tauri::command]
fn copy_token(steamid: String) -> Result<String, String> {
    steam::write_clipboard(&steam::token_for(&steamid)?)?;
    Ok("Login code copied.".to_string())
}

#[tauri::command]
fn copy_text(text: String) -> Result<(), String> {
    steam::write_clipboard(&text)
}

/// Opens the account's Steam profile in the default browser.
///
/// Goes through `rundll32 url.dll,FileProtocolHandler` rather than `cmd /C start`
/// so no shell parses the argument, and the SteamID is checked to be digits only —
/// between the two there is nothing for a crafted value to escape into.
#[tauri::command]
fn open_profile(steamid: String) -> Result<(), String> {
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
fn prune_expired(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::prune_expired_tokens())
}

/// Asks GitHub whether a newer release exists. Never downloads anything.
#[tauri::command]
fn check_for_update() -> Result<update::UpdateInfo, String> {
    update::check(env!("CARGO_PKG_VERSION"))
}

#[tauri::command]
fn app_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

/// Recent quiet failures — the ones that never became a toast.
#[tauri::command]
fn get_log() -> Vec<String> {
    log::entries()
}

#[tauri::command]
fn clear_log() {
    log::clear();
}

#[tauri::command]
fn open_url(url: String) -> Result<(), String> {
    // Only our own release pages — this command exists for the update dialog, and
    // a general "open any URL" bridge is a thing worth not having.
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
fn sign_in(app: AppHandle, steamid: String) -> Result<String, String> {
    let result = finish_account_op(&app, steam::handle_login_account(&find_account(&steamid)?));
    if result.is_ok() {
        metadata::touch_last_used(&steamid);
    }
    result
}

#[tauri::command]
fn remove_account(app: AppHandle, steamid: String) -> Result<String, String> {
    let result = finish_account_op(&app, steam::handle_delete_account(&find_account(&steamid)?));
    if result.is_ok() {
        // Otherwise a re-imported account inherits the tag and cooldown of the one
        // the user just deleted.
        metadata::forget(&steamid);
    }
    result
}

/// Colour tags, cooldowns and last-used times, keyed by SteamID64.
#[tauri::command]
fn get_metadata() -> metadata::MetaMap {
    metadata::load()
}


#[tauri::command]
fn set_account_color(app: AppHandle, steamid: String, color: String) -> Result<(), String> {
    metadata::set_color(&steamid, &color)?;
    let _ = app.emit("metadata-changed", ());
    Ok(())
}

#[tauri::command]
fn set_account_cooldown(
    app: AppHandle,
    steamid: String,
    until: Option<i64>,
) -> Result<(), String> {
    metadata::set_cooldown(&steamid, until)?;
    let _ = app.emit("metadata-changed", ());
    Ok(())
}

#[tauri::command]
fn clear_steam(app: AppHandle) -> Result<String, String> {
    finish_account_op(&app, steam::handle_clear_steam())
}

#[tauri::command]
fn get_settings() -> settings::AppSettings {
    settings::load_settings()
}

#[tauri::command]
fn save_settings(app: AppHandle, settings: settings::AppSettings) -> Result<(), String> {
    settings::save_settings(&settings)?;
    capture::apply_to_main(&app, settings.hide_from_capture);
    let _ = tray::rebuild(&app);
    let _ = app.emit("settings-changed", ());
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            tray::setup(app.handle())?;
            capture::apply_to_main(app.handle(), settings::load_settings().hide_from_capture);
            window_state::restore(app.handle());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_accounts,
            fetch_account_intel,
            validate_api_key,
            read_clipboard,
            import_clipboard,
            import_account,
            sign_in,
            remove_account,
            clear_steam,
            get_settings,
            save_settings,
            get_metadata,
            set_account_color,
            set_account_cooldown,
            import_from_file,
            import_from_steam_cache,
            export_tokens,
            export_tokens_to_file,
            copy_token,
            copy_text,
            open_profile,
            prune_expired,
            check_for_update,
            app_version,
            open_url,
            get_log,
            clear_log
        ])
        .run(tauri::generate_context!())
        .expect("error while running nfa.pub tool");
}
