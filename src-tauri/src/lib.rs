mod settings;
mod steam;
mod tray;

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
}

impl From<&SteamAccount> for AccountDto {
    fn from(a: &SteamAccount) -> Self {
        AccountDto {
            steamid: a.steamid.clone(),
            account_name: a.account_name.clone(),
            persona_name: a.persona_name.clone(),
            display_name: a.display_name().to_string(),
            initials: a.initials(),
            most_recent: a.most_recent,
            avatar: a.avatar_path.as_ref().and_then(|p| avatar_data_url(p)),
        }
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
    Ok(steam::load_steam_accounts()?
        .iter()
        .map(AccountDto::from)
        .collect())
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

#[tauri::command]
fn sign_in(app: AppHandle, steamid: String) -> Result<String, String> {
    finish_account_op(&app, steam::handle_login_account(&find_account(&steamid)?))
}

#[tauri::command]
fn remove_account(app: AppHandle, steamid: String) -> Result<String, String> {
    finish_account_op(&app, steam::handle_delete_account(&find_account(&steamid)?))
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
    let _ = tray::rebuild(&app);
    let _ = app.emit("settings-changed", ());
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .setup(|app| {
            tray::setup(app.handle())?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_accounts,
            read_clipboard,
            import_clipboard,
            import_account,
            sign_in,
            remove_account,
            clear_steam,
            get_settings,
            save_settings
        ])
        .run(tauri::generate_context!())
        .expect("error while running archievable");
}
