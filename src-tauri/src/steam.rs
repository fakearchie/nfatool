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
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use crate::settings::{load_settings, AppSettings};

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

pub fn import_from_steam_cache() -> Result<String, String> {
    let local_vdf = paths::local_steam_cache_path()?.join("local.vdf");
    let content = fs::read_to_string(&local_vdf)
        .map_err(|_| "Steam has no cached logins on this PC yet.".to_string())?;

    let entries = config::read_connect_cache(&content);
    if entries.is_empty() {
        return Err("Steam's login cache is empty.".to_string());
    }

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
    config::disable_user_chooser(&config_dir.join("config.vdf"))?;
    config::apply_localconfig_settings(steamid, steam_path, settings)?;
    for problem in cs2::apply_on_login(steamid, steam_path, settings) {
        crate::log::record(format!("CS2 setup: {problem}"));
    }
    if settings.cancel_downloads_on_login {
        downloads::defer_game_updates(steam_path);
    }
    process::write_autologin_user(username)
}

fn relaunch_steam(steam_path: &str, settings: &AppSettings) -> Result<(), String> {
    // The log already holds every past logon, including old refusals, so record how
    // long it is before Steam can append to it. Anything past this mark is ours.
    LOG_MARK.store(connection_log_len(), Ordering::Relaxed);
    std::thread::sleep(Duration::from_millis(400));
    process::launch_steam(steam_path, settings)?;
    Ok(())
}

pub fn dpapi_protect_token(token: &str, steamid: &str) -> Result<String, String> {
    crypto::dpapi_protect(token, steamid)
}

pub fn dpapi_unprotect_token(stored: &str, steamid: &str) -> Result<String, String> {
    crypto::dpapi_unprotect(stored, steamid)
}

pub fn all_tokens_plaintext() -> Result<Vec<(String, String, String, String)>, String> {
    Ok(tokens::load_records()
        .into_iter()
        .map(|(steamid, r)| (steamid, r.account_name, r.persona_name, r.token))
        .collect())
}

pub fn rewrite_tokens(records: &[(String, String, String, String)]) -> Result<(), String> {
    tokens::rewrite_all(records)
}

static LOG_MARK: AtomicU64 = AtomicU64::new(0);

pub enum SignInCheck {
    Confirmed,
    Rejected,
    OtherAccount,
    Unknown,
}

fn connection_log_path() -> Option<PathBuf> {
    let path = PathBuf::from(paths::get_steam_path().ok()?)
        .join("logs")
        .join("connection_log.txt");
    path.exists().then_some(path)
}

fn connection_log_len() -> u64 {
    connection_log_path()
        .and_then(|p| fs::metadata(p).ok())
        .map(|m| m.len())
        .unwrap_or(0)
}

/// Reads what Steam wrote to its connection log since we launched it.
///
/// Steam rotates the log, so a file shorter than the mark means it started over and
/// everything in it is new.
fn log_since_mark() -> String {
    use std::io::{Read, Seek, SeekFrom};

    let Some(path) = connection_log_path() else {
        return String::new();
    };
    let Ok(mut file) = fs::File::open(path) else {
        return String::new();
    };
    let mark = LOG_MARK.load(Ordering::Relaxed);
    let len = file.metadata().map(|m| m.len()).unwrap_or(0);
    if len > mark && file.seek(SeekFrom::Start(mark)).is_err() {
        return String::new();
    }
    let mut buffer = Vec::new();
    if file.read_to_end(&mut buffer).is_err() {
        return String::new();
    }
    String::from_utf8_lossy(&buffer).into_owned()
}

/// Steam's own verdict on the logon, read out of its connection log.
///
/// Every line is prefixed with the account it concerns, as `[U:1:<steamid3>]`, and
/// the result is an EResult name in quotes. Those names are Steam's internal
/// identifiers and are not translated, unlike anything it puts on screen.
fn verdict_in_log(text: &str, expected: u32) -> Option<SignInCheck> {
    let ours = format!("[U:1:{expected}]");

    for line in text.lines() {
        if !line.contains("RecvMsgClientLogOnResponse()") {
            continue;
        }
        if !line.contains(&ours) {
            // A response for somebody else means Steam signed in as another account.
            if line.contains("'OK'") && line.contains("[U:1:") {
                return Some(SignInCheck::OtherAccount);
            }
            continue;
        }
        if line.contains("'OK'") {
            return Some(SignInCheck::Confirmed);
        }
        // 'Access Denied' is the refusal for a revoked or expired login code. Other
        // results ('Try another CM', 'Failure') are transient and Steam retries them,
        // so they are deliberately not treated as an answer.
        if line.contains("'Access Denied'") {
            return Some(SignInCheck::Rejected);
        }
    }
    None
}

/// Waits for Steam to say whether it accepted the login code.
///
/// Steam's log is the only signal that distinguishes "still starting up" from "the
/// code was refused". Guessing from elapsed time instead reported a refusal for any
/// Steam that took longer than the timeout to sign in, which then offered to delete
/// a perfectly good account.
pub fn wait_for_sign_in(steamid: &str) -> SignInCheck {
    const MAX_WAIT: Duration = Duration::from_secs(90);

    let Ok(expected) = paths::steamid64_to_steamid3(steamid)
        .and_then(|s| s.parse::<u32>().map_err(|_| "bad steamid3".to_string()))
    else {
        return SignInCheck::Confirmed;
    };

    let start = std::time::Instant::now();

    while start.elapsed() < MAX_WAIT {
        // The registry is the cheaper confirmation and settles first on a fast sign-in.
        match process::active_user() {
            0 => {}
            id if id == expected => return SignInCheck::Confirmed,
            _ => return SignInCheck::OtherAccount,
        }

        if let Some(verdict) = verdict_in_log(&log_since_mark(), expected) {
            return verdict;
        }

        std::thread::sleep(Duration::from_millis(500));
    }

    // Steam never answered. Saying nothing is right here: an unanswered logon is not
    // evidence that the code is dead.
    SignInCheck::Unknown
}

#[cfg(test)]
mod sign_in_log_tests {
    use super::*;

    // Verbatim from Steam's own connection_log.txt.
    const REFUSED: &str = "\
[2026-09-10 12:22:49] [Logging On, 4, 7] [U:1:301155508] RecvMsgClientLogOnResponse() : [I:0:0] 'Access Denied'
[2026-09-10 12:22:49] Clearing in-memory token - 15 (Access Denied): LogonFailureReceived(2)
[2026-09-10 12:22:50] [Logged Off, 4, 0] [U:1:301155508] ConnectionDisconnected() not auto reconnecting due to Access Denied
";

    const ACCEPTED: &str = "\
[2026-09-10 12:23:15] [Connected, 4, 7] [U:1:1595689475] Logging on [U:1:1595689475]
[2026-09-10 12:23:15] [Logging On, 4, 7] [U:1:1595689475] RecvMsgClientLogOnResponse() : [U:1:1595689475] 'OK'
[2026-09-10 12:23:15] [Logged On, 4, 7] [U:1:1595689475] RecvMsgClientLogOnResponse() : processing complete
";

    const STILL_CONNECTING: &str = "\
[2026-09-10 12:23:14] GetCMListForConnect -- DC 'iad1' count: 4
[2026-09-10 12:23:15] [Connecting, 4, 0] [U:1:1595689475] PingWebSocketCM() (cmp2-fra1.steamserver.net:27018) starting...
[2026-09-10 12:23:15] [Connected, 4, 7] [U:1:1595689475] Logging on [U:1:1595689475]
";

    fn verdict(text: &str, id: u32) -> Option<&'static str> {
        verdict_in_log(text, id).map(|v| match v {
            SignInCheck::Confirmed => "ok",
            SignInCheck::Rejected => "rejected",
            SignInCheck::OtherAccount => "other",
            SignInCheck::Unknown => "unknown",
        })
    }

    #[test]
    fn a_refused_code_is_rejected() {
        assert_eq!(verdict(REFUSED, 301155508), Some("rejected"));
    }

    #[test]
    fn an_accepted_code_is_confirmed() {
        assert_eq!(verdict(ACCEPTED, 1595689475), Some("ok"));
    }

    /// The whole point of the rewrite: while Steam is still connecting there is no
    /// answer yet, and reporting one offered to delete a working account.
    #[test]
    fn a_slow_start_gives_no_verdict() {
        assert_eq!(verdict(STILL_CONNECTING, 1595689475), None);
    }

    #[test]
    fn another_accounts_refusal_is_not_ours() {
        assert_eq!(verdict(REFUSED, 1595689475), None);
    }

    #[test]
    fn signing_in_as_somebody_else_is_reported() {
        assert_eq!(verdict(ACCEPTED, 301155508), Some("other"));
    }

    /// 'Try another CM' and 'Failure' are retried by Steam, so they are not an answer.
    #[test]
    fn transient_failures_are_not_a_refusal() {
        let text = "[2026-09-10 12:22:49] [Logging On, 4, 7] [U:1:301155508] RecvMsgClientLogOnResponse() : 'Try another CM' / 'Failure'\n";
        assert_eq!(verdict(text, 301155508), None);
    }
}
