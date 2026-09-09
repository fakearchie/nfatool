mod account;
mod config;
mod cs2;
mod crypto;
mod downloads;
mod import;
mod paths;
mod process;
mod tokens;
mod vdf;
mod webapi;

pub use account::{avatar_cdn_url, load_steam_accounts, SteamAccount};
pub use webapi::{AccountIntel, Map as IntelMap};
pub use import::{read_clipboard, token_expiry, write_clipboard};

use std::fs;
use std::path::Path;
use std::time::Duration;

use crate::settings::{load_settings, AppSettings};

/// Reads public account data for the given SteamIDs. Uses the user's Web API key
/// when they have set one, otherwise falls back to the public profile XML, which
/// only carries online status.
pub fn fetch_account_intel(steamids: &[String]) -> Result<IntelMap, String> {
    let settings = load_settings();
    let key = settings.steam_api_key.trim().to_string();
    if key.is_empty() {
        Ok(webapi::fetch_without_key(steamids))
    } else {
        webapi::fetch_with_key(&key, steamids)
    }
}

pub fn validate_api_key(key: &str) -> Result<bool, String> {
    webapi::validate_key(key)
}

/// Imports every account already signed in on this PC by reading the tokens out of
/// Steam's own `local.vdf` ConnectCache — no login codes needed.
///
/// The cache is keyed by `crc32(account_name)`, so account names come from
/// `loginusers.vdf` and are matched by hash. Each blob is DPAPI-sealed to the
/// current Windows user; one that won't open belongs to someone else and is
/// skipped rather than reported.
pub fn import_from_steam_cache() -> Result<String, String> {
    let local_vdf = paths::local_steam_cache_path()?.join("local.vdf");
    let content = fs::read_to_string(&local_vdf)
        .map_err(|_| "Steam has no cached logins on this PC yet.".to_string())?;

    let entries = config::read_connect_cache(&content);
    if entries.is_empty() {
        return Err("Steam's login cache is empty.".to_string());
    }

    // crc32 -> account name, for every account Steam knows about.
    let accounts = load_steam_accounts()?;
    let by_crc: std::collections::HashMap<String, String> = accounts
        .iter()
        .filter(|a| !a.account_name.is_empty())
        .map(|a| (crypto::compute_crc32(&a.account_name), a.account_name.clone()))
        .collect();

    let existing = tokens::load_records();
    let mut imported = 0usize;
    let mut skipped = 0usize;

    for (crc, blob) in entries {
        let Some(account_name) = by_crc.get(&crc) else {
            skipped += 1;
            continue;
        };
        let Ok(plain) = crypto::steam_decrypt(&blob, account_name) else {
            skipped += 1;
            continue;
        };
        let Some(jwt) = import::extract_jwt_token(&plain) else {
            skipped += 1;
            continue;
        };
        let Ok(steamid) = import::extract_steamid_from_jwt(&jwt) else {
            skipped += 1;
            continue;
        };
        // Already holding this exact token — nothing to do.
        if existing.get(&steamid).is_some_and(|r| r.token == jwt) {
            continue;
        }
        tokens::save_record(&steamid, account_name, account_name, &jwt);
        imported += 1;
    }

    if imported == 0 {
        return Err(if skipped > 0 {
            "No new accounts found in Steam's login cache.".to_string()
        } else {
            "Every cached account is already saved.".to_string()
        });
    }
    Ok(format!(
        "Imported {imported} account{} from Steam's cache.",
        if imported == 1 { "" } else { "s" }
    ))
}

/// `username||JWT`, one per line — the same format the import box accepts, so an
/// export can be pasted straight back in.
pub fn export_tokens(steamids: &[String]) -> Result<String, String> {
    let records = tokens::load_records();
    let mut lines = Vec::new();
    for steamid in steamids {
        if let Some(rec) = records.get(steamid) {
            if !rec.token.is_empty() {
                lines.push(format!("{}||{}", rec.account_name, rec.token));
            }
        }
    }
    if lines.is_empty() {
        return Err("None of those accounts have a saved token to export.".to_string());
    }
    Ok(lines.join("\n"))
}

/// Drops records whose token has expired. Tokens with no readable `exp` are kept:
/// "can't tell" must never be treated as "dead".
pub fn prune_expired_tokens() -> Result<String, String> {
    let records = tokens::load_records();
    let now = crate::metadata::now_unix();
    let dead: Vec<String> = records
        .iter()
        .filter(|(_, rec)| !rec.token.is_empty() && import::token_expired(&rec.token, now))
        .map(|(steamid, _)| steamid.clone())
        .collect();

    if dead.is_empty() {
        return Ok("No expired tokens.".to_string());
    }
    for steamid in &dead {
        tokens::remove_record(steamid);
    }
    Ok(format!(
        "Removed {} expired token{}.",
        dead.len(),
        if dead.len() == 1 { "" } else { "s" }
    ))
}

/// The saved token for one account, for the panel's "copy token" action.
pub fn token_for(steamid: &str) -> Result<String, String> {
    tokens::load_records()
        .get(steamid)
        .map(|r| r.token.clone())
        .filter(|t| !t.is_empty())
        .ok_or_else(|| "No saved token for this account.".to_string())
}

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
        relaunch_steam(&steam_path_string, &settings)?;
    }

    let mut msg = format!("Imported {imported} accounts.");
    if !errors.is_empty() {
        msg.push_str(&format!(" {} failed.", errors.len()));
    }
    Ok(msg)
}

fn import_account_files(entry: &str, steam_path: &Path) -> Result<(String, String), String> {
    let (username, jwt) = import::parse_clipboard(entry)?;
    let steamid = import::extract_steamid_from_jwt(&jwt)?;
    config::write_account_files(&username, &jwt, &steamid, steam_path)?;
    tokens::save_record(&steamid, &username, &username, &jwt);
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
    tokens::save_record(&steamid, &username, &username, &jwt);
    let settings = load_settings();
    apply_active_account(&username, &steamid, steam_path, &settings)?;
    relaunch_steam(&steam_path_string, &settings)?;

    Ok(format!("Imported {username}."))
}

pub fn handle_login_account(account: &SteamAccount) -> Result<String, String> {
    let steam_path_string = paths::get_steam_path()?;
    let steam_path = Path::new(&steam_path_string);
    let settings = load_settings();

    process::stop_steam()?;

    // Re-provision the ConnectCache token + config.vdf from our stored copy on
    // every sign-in, so switching works even if Steam's cache was cleared since
    // import. Accounts imported before token persistence fall back to the flip.
    if let Some(jwt) = &account.token {
        config::check_steam_config_files(&steam_path.join("config"))?;
        config::write_account_files(&account.account_name, jwt, &account.steamid, steam_path)?;
    }

    apply_active_account(&account.account_name, &account.steamid, steam_path, &settings)?;
    relaunch_steam(&steam_path_string, &settings)?;

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
    tokens::remove_record(&account.steamid);

    Ok(format!("Removed {}.", account.display_name()))
}

pub fn handle_clear_steam() -> Result<String, String> {
    // Non-destructive: wipe only the cached login tokens (local.vdf ConnectCache),
    // which signs Steam out, while leaving the rest of %LOCALAPPDATA%\Steam and the
    // account list intact. Saved accounts can be signed back in from their stored
    // token, so this is recoverable rather than a full re-import.
    process::stop_steam()?;
    let base_path = paths::local_steam_cache_path()?;
    config::clear_login_cache(&base_path)?;
    Ok("Steam signed out on this PC.".to_string())
}

fn apply_active_account(
    username: &str,
    steamid: &str,
    steam_path: &Path,
    settings: &AppSettings,
) -> Result<(), String> {
    let config_dir = steam_path.join("config");
    let loginusers_vdf = config_dir.join("loginusers.vdf");
    config::update_loginusers_vdf(&loginusers_vdf, username, steamid)?;
    // Otherwise Steam greets you with its own account picker and you choose twice.
    config::disable_user_chooser(&config_dir.join("config.vdf"))?;
    config::apply_localconfig_settings(steamid, steam_path, settings)?;
    // Best-effort, and after localconfig exists: none of the CS2 extras is worth
    // failing a sign-in over, so problems are logged rather than propagated.
    for problem in cs2::apply_on_login(steamid, steam_path, settings) {
        crate::log::record(format!("CS2 setup: {problem}"));
    }
    if settings.cancel_downloads_on_login {
        // Best-effort: defer background game updates so signing in doesn't kick
        // off downloads. Runs while Steam is dead (we just stopped it).
        downloads::defer_game_updates(steam_path);
    }
    process::write_autologin_user(username)
}

fn relaunch_steam(steam_path: &str, settings: &AppSettings) -> Result<(), String> {
    std::thread::sleep(Duration::from_millis(400));
    process::launch_steam(steam_path, settings)?;
    Ok(())
}
