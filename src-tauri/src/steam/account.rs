use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use super::paths::{get_steam_path, steamid64_to_steamid3};
use super::vdf::quoted_fields;

#[derive(Clone, Default, PartialEq)]
pub struct SteamAccount {
    pub steamid: String,
    pub account_name: String,
    pub persona_name: String,
    pub avatar_hash: Option<String>,
    pub avatar_path: Option<PathBuf>,
    pub most_recent: bool,
    pub timestamp: Option<String>,
    // Raw JWT from our own store, when available. Never sent to the frontend;
    // used to re-provision Steam on sign-in. None for accounts we only see in
    // Steam's loginusers.vdf (e.g. imported before token persistence).
    pub token: Option<String>,
}

impl SteamAccount {
    pub fn display_name(&self) -> &str {
        if !self.persona_name.is_empty() {
            &self.persona_name
        } else {
            &self.account_name
        }
    }

    pub fn initials(&self) -> String {
        let chars: String = self
            .display_name()
            .chars()
            .filter(|c| c.is_alphanumeric())
            .take(2)
            .collect::<String>()
            .to_uppercase();
        if chars.is_empty() {
            "?".to_string()
        } else {
            chars
        }
    }
}

pub fn load_steam_accounts() -> Result<Vec<SteamAccount>, String> {
    let steam_path = get_steam_path()?;
    let steam_path = Path::new(&steam_path);
    let loginusers_path = steam_path.join("config").join("loginusers.vdf");
    // Missing/unreadable loginusers.vdf is not fatal — our own records below may
    // still list accounts (e.g. right after a login-cache reset).
    let content = fs::read_to_string(&loginusers_path).unwrap_or_default();

    let mut accounts = parse_loginusers(&content);

    // Merge the app's persistent records: attach stored tokens to known accounts,
    // and surface any saved account Steam has since forgotten so it can be signed
    // back in from its token.
    let records = super::tokens::load_records();
    let mut seen: HashSet<String> = accounts.iter().map(|a| a.steamid.clone()).collect();
    for account in &mut accounts {
        if let Some(rec) = records.get(&account.steamid) {
            account.token = Some(rec.token.clone());
            if account.account_name.is_empty() {
                account.account_name = rec.account_name.clone();
            }
            if account.persona_name.is_empty() {
                account.persona_name = rec.persona_name.clone();
            }
        }
    }
    for (steamid, rec) in &records {
        if seen.insert(steamid.clone()) {
            accounts.push(SteamAccount {
                steamid: steamid.clone(),
                account_name: rec.account_name.clone(),
                persona_name: rec.persona_name.clone(),
                token: Some(rec.token.clone()),
                ..Default::default()
            });
        }
    }

    for account in &mut accounts {
        account.avatar_path = find_avatar_path(steam_path, account);
    }
    accounts.sort_by(|a, b| {
        b.most_recent
            .cmp(&a.most_recent)
            .then(a.display_name().cmp(b.display_name()))
    });
    Ok(accounts)
}

fn parse_loginusers(content: &str) -> Vec<SteamAccount> {
    let mut accounts = Vec::new();
    let mut current: Option<SteamAccount> = None;

    for line in content.lines() {
        let fields = quoted_fields(line);
        if fields.len() == 1 && is_steamid64(&fields[0]) {
            current = Some(SteamAccount {
                steamid: fields[0].clone(),
                ..Default::default()
            });
            continue;
        }

        if line.trim() == "}" {
            if let Some(account) = current.take() {
                if !account.steamid.is_empty() {
                    accounts.push(account);
                }
            }
            continue;
        }

        if let Some(account) = current.as_mut() {
            if fields.len() >= 2 {
                match fields[0].as_str() {
                    "AccountName" => account.account_name = fields[1].clone(),
                    "PersonaName" => account.persona_name = fields[1].clone(),
                    "Avatar" => account.avatar_hash = Some(fields[1].clone()),
                    "MostRecent" => account.most_recent = fields[1] == "1",
                    "Timestamp" => account.timestamp = Some(fields[1].clone()),
                    _ => {}
                }
            }
        }
    }

    accounts
}

fn find_avatar_path(steam_path: &Path, account: &SteamAccount) -> Option<PathBuf> {
    if let Some(path) = find_in_avatarcache(steam_path, account) {
        return Some(path);
    }

    if let Ok(steamid3) = steamid64_to_steamid3(&account.steamid) {
        let userdata = steam_path.join("userdata").join(steamid3);
        for candidate in [
            userdata.join("config").join("avatar.png"),
            userdata.join("config").join("avatar.jpg"),
            userdata.join("avatar.png"),
        ] {
            if candidate.exists() {
                return Some(candidate);
            }
        }
    }

    None
}

fn find_in_avatarcache(steam_path: &Path, account: &SteamAccount) -> Option<PathBuf> {
    let avatar_dir = steam_path.join("config").join("avatarcache");
    if !avatar_dir.exists() {
        return None;
    }

    let hash = account.avatar_hash.as_deref();
    let steamid = account.steamid.as_str();

    if let Some(hash) = hash {
        for name in [
            hash.to_string(),
            format!("{hash}.png"),
            format!("{hash}.jpg"),
            format!("{hash}.jpeg"),
            format!("{hash}.avatar"),
        ] {
            let path = avatar_dir.join(&name);
            if path.exists() {
                return Some(path);
            }
        }
    }

    for extension in ["png", "jpg", "jpeg", ""] {
        let path = if extension.is_empty() {
            avatar_dir.join(steamid)
        } else {
            avatar_dir.join(format!("{steamid}.{extension}"))
        };
        if path.exists() {
            return Some(path);
        }
    }

    let hash_lower = hash.map(str::to_lowercase);
    let entries = fs::read_dir(&avatar_dir).ok()?;
    for entry in entries.flatten() {
        let path = entry.path();
        let file_name = path.file_name()?.to_string_lossy().to_lowercase();
        let stem = path
            .file_stem()
            .map(|s| s.to_string_lossy().to_lowercase())
            .unwrap_or_default();

        let matches_hash = hash_lower
            .as_ref()
            .is_some_and(|h| stem == *h || file_name.contains(h.as_str()));
        let matches_steamid = file_name.contains(steamid);

        if matches_hash || matches_steamid {
            return Some(path);
        }
    }

    None
}

fn is_steamid64(value: &str) -> bool {
    value.len() >= 16 && value.chars().all(|c| c.is_ascii_digit())
}
