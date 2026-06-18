mod account;
mod config;
mod crypto;
mod import;
mod paths;
mod process;
mod vdf;

pub use account::{load_steam_accounts, SteamAccount};
pub use import::read_clipboard;

use std::path::Path;
use std::time::Duration;

use crate::settings::{load_settings, AppSettings};

pub fn import_from_clipboard() -> Result<String, String> {
    let content = read_clipboard()?;
    handle_batch_import(&content)
}

pub fn handle_batch_import(content: &str) -> Result<String, String> {
    let entries = import::split_batch_payloads(content);
    if entries.is_empty() {
        return Err("Clipboard is empty.".to_string());
    }
    if entries.len() == 1 {
        return import_single_account(&entries[0]);
    }

    let steam_path_string = paths::get_steam_path()?;
    let steam_path = Path::new(&steam_path_string);
    config::check_steam_config_files(&steam_path.join("config"))?;
    process::stop_steam()?;

    let mut imported = 0usize;
    let mut errors = Vec::new();
    let mut last: Option<(String, String)> = None;

    for (idx, entry) in entries.iter().enumerate() {
        match import_account_files(entry, steam_path) {
            Ok((username, steamid)) => {
                imported += 1;
                last = Some((username, steamid));
            }
            Err(e) => errors.push(format!("#{}: {}", idx + 1, e)),
        }
    }

    if imported == 0 {
        return Err(errors.join(" | "));
    }

    if let Some((username, steamid)) = last {
        let settings = load_settings();
        apply_active_account(&username, &steamid, steam_path, &settings)?;
        relaunch_steam(&steam_path_string, &steamid, &settings)?;
    }

    let mut msg = format!("Imported {imported} accounts. Starting Steam.");
    if !errors.is_empty() {
        msg.push_str(&format!(" {} failed.", errors.len()));
    }
    Ok(msg)
}

fn import_account_files(entry: &str, steam_path: &Path) -> Result<(String, String), String> {
    let (username, jwt) = import::parse_clipboard(entry)?;
    let steamid = import::extract_steamid_from_jwt(&jwt)?;
    config::write_account_files(&username, &jwt, &steamid, steam_path)?;
    Ok((username, steamid))
}

fn import_single_account(content: &str) -> Result<String, String> {
    let (username, jwt) = import::parse_clipboard(content)?;
    let steamid = import::extract_steamid_from_jwt(&jwt)?;

    let steam_path_string = paths::get_steam_path()?;
    let steam_path = Path::new(&steam_path_string);
    config::check_steam_config_files(&steam_path.join("config"))?;
    process::stop_steam()?;

    config::write_account_files(&username, &jwt, &steamid, steam_path)?;
    let settings = load_settings();
    apply_active_account(&username, &steamid, steam_path, &settings)?;
    relaunch_steam(&steam_path_string, &steamid, &settings)?;

    Ok(format!("Imported {username}. Starting Steam."))
}

pub fn handle_login_account(account: &SteamAccount) -> Result<String, String> {
    let steam_path_string = paths::get_steam_path()?;
    let steam_path = Path::new(&steam_path_string);
    let settings = load_settings();

    process::stop_steam()?;
    apply_active_account(&account.account_name, &account.steamid, steam_path, &settings)?;
    relaunch_steam(&steam_path_string, &account.steamid, &settings)?;

    Ok(format!(
        "Signed in as {}. Starting Steam.",
        account.display_name()
    ))
}

pub fn handle_delete_account(account: &SteamAccount) -> Result<String, String> {
    let steam_path_string = paths::get_steam_path()?;
    let steam_path = Path::new(&steam_path_string);
    process::stop_steam()?;

    config::remove_loginuser(
        &steam_path.join("config").join("loginusers.vdf"),
        &account.steamid,
    )?;

    let config_vdf = steam_path.join("config").join("config.vdf");
    if config_vdf.exists() {
        config::remove_config_account(&config_vdf, &account.steamid)?;
    }

    if let Ok(local_dir) = paths::local_steam_cache_path() {
        let local_vdf = local_dir.join("local.vdf");
        if local_vdf.exists() {
            let crc = crypto::compute_crc32(&account.account_name);
            if let Ok(content) = std::fs::read_to_string(&local_vdf) {
                let updated = config::remove_connect_cache_entry(&content, &crc);
                let _ = std::fs::write(&local_vdf, updated);
            }
        }
    }

    process::clear_autologin_if_matches(&account.account_name);

    Ok(format!("Removed {}.", account.display_name()))
}

pub fn handle_clear_steam() -> Result<String, String> {
    let steam_path = paths::get_steam_path()?;
    let config_dir = Path::new(&steam_path).join("config");
    process::stop_steam()?;
    let base_path = paths::local_steam_cache_path()?;
    config::delete_steam_files_and_folder(&config_dir, &base_path)?;
    Ok("Steam cache cleared.".to_string())
}

fn apply_active_account(
    username: &str,
    steamid: &str,
    steam_path: &Path,
    settings: &AppSettings,
) -> Result<(), String> {
    let loginusers_vdf = steam_path.join("config").join("loginusers.vdf");
    config::update_loginusers_vdf(&loginusers_vdf, username, steamid)?;
    config::apply_localconfig_settings(steamid, steam_path, settings)?;
    process::write_autologin_user(username)
}

fn relaunch_steam(steam_path: &str, steamid: &str, settings: &AppSettings) -> Result<(), String> {
    std::thread::sleep(Duration::from_millis(400));
    let localconfig = paths::localconfig_path(
        Path::new(steam_path),
        &paths::steamid64_to_steamid3(steamid)?,
    );
    process::launch_steam(steam_path, settings)?;
    if settings.cancel_downloads_on_login {
        config::schedule_download_pause_retry(localconfig);
    }
    Ok(())
}
