
use std::collections::HashMap;
use std::time::Duration;

use serde::Serialize;
use serde_json::Value;

const API: &str = "https://api.steampowered.com";
const TIMEOUT: Duration = Duration::from_secs(10);

const BATCH: usize = 100;

const XML_THROTTLE: Duration = Duration::from_millis(400);

#[derive(Debug, Clone, Default, Serialize)]
pub struct AccountIntel {
    pub persona_name: Option<String>,
    pub avatar_hash: Option<String>,
    pub persona_state: Option<u8>,
    pub game: Option<String>,
    pub level: Option<u32>,
    pub vac_banned: bool,
    pub vac_bans: u32,
    pub game_bans: u32,
    pub community_banned: bool,
    pub trade_ban: Option<String>,
    pub banned: bool,
}

impl AccountIntel {
    fn recompute_banned(&mut self) {
        self.banned = self.vac_banned
            || self.vac_bans > 0
            || self.game_bans > 0
            || self.community_banned
            || matches!(self.trade_ban.as_deref(), Some("banned") | Some("probation"));
    }
}

fn get_json(url: &str) -> Result<Value, String> {
    let body = ureq::get(url)
        .timeout(TIMEOUT)
        .call()
        .map_err(http_error)?
        .into_string()
        .map_err(|e| format!("Could not read Steam's reply: {e}"))?;
    serde_json::from_str(&body).map_err(|e| format!("Steam sent malformed JSON: {e}"))
}

fn http_error(e: ureq::Error) -> String {
    match e {
        ureq::Error::Status(401, _) | ureq::Error::Status(403, _) => {
            "Steam rejected the API key.".to_string()
        }
        ureq::Error::Status(429, _) => {
            "Steam is rate-limiting this key. Try again in a minute.".to_string()
        }
        ureq::Error::Status(code, _) => format!("Steam returned HTTP {code}."),
        ureq::Error::Transport(t) => format!("Could not reach Steam: {t}"),
    }
}

pub fn validate_key(key: &str) -> Result<bool, String> {
    let key = key.trim();
    if key.len() != 32 || !key.chars().all(|c| c.is_ascii_hexdigit()) {
        return Ok(false);
    }
    let url = format!("{API}/ISteamWebAPIUtil/GetSupportedAPIList/v1/?key={key}");
    match ureq::get(&url).timeout(TIMEOUT).call() {
        Ok(_) => Ok(true),
        Err(ureq::Error::Status(401, _)) | Err(ureq::Error::Status(403, _)) => Ok(false),
        Err(e) => Err(http_error(e)),
    }
}

pub fn fetch_with_key(key: &str, steamids: &[String]) -> Result<Map, String> {
    let mut out: Map = HashMap::new();
    for chunk in steamids.chunks(BATCH) {
        merge_summaries(key, chunk, &mut out)?;
        merge_bans(key, chunk, &mut out)?;
    }
    merge_levels(key, steamids, &mut out);
    for intel in out.values_mut() {
        intel.recompute_banned();
    }
    Ok(out)
}

pub type Map = HashMap<String, AccountIntel>;

fn merge_summaries(key: &str, ids: &[String], out: &mut Map) -> Result<(), String> {
    let url = format!(
        "{API}/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={}",
        ids.join(",")
    );
    let json = get_json(&url)?;
    let players = json["response"]["players"].as_array().cloned().unwrap_or_default();
    for p in players {
        let Some(id) = p["steamid"].as_str() else {
            continue;
        };
        let entry = out.entry(id.to_string()).or_default();
        entry.persona_name = p["personaname"].as_str().map(str::to_string);
        entry.avatar_hash = p["avatarhash"].as_str().map(str::to_string);
        entry.persona_state = p["personastate"].as_u64().map(|n| n as u8);
        entry.game = p["gameextrainfo"].as_str().map(str::to_string);
    }
    Ok(())
}

fn merge_bans(key: &str, ids: &[String], out: &mut Map) -> Result<(), String> {
    let url = format!(
        "{API}/ISteamUser/GetPlayerBans/v1/?key={key}&steamids={}",
        ids.join(",")
    );
    let json = get_json(&url)?;
    let players = json["players"].as_array().cloned().unwrap_or_default();
    for p in players {
        let Some(id) = p["SteamId"].as_str() else {
            continue;
        };
        let entry = out.entry(id.to_string()).or_default();
        entry.vac_banned = p["VACBanned"].as_bool().unwrap_or(false);
        entry.vac_bans = p["NumberOfVACBans"].as_u64().unwrap_or(0) as u32;
        entry.game_bans = p["NumberOfGameBans"].as_u64().unwrap_or(0) as u32;
        entry.community_banned = p["CommunityBanned"].as_bool().unwrap_or(false);
        entry.trade_ban = p["EconomyBan"].as_str().map(str::to_string);
    }
    Ok(())
}

fn merge_levels(key: &str, ids: &[String], out: &mut Map) {
    let results: Vec<(String, Option<u32>)> = std::thread::scope(|scope| {
        let handles: Vec<_> = ids
            .iter()
            .map(|id| {
                scope.spawn(move || {
                    let url = format!(
                        "{API}/IPlayerService/GetSteamLevel/v1/?key={key}&steamid={id}"
                    );
                    let level = get_json(&url)
                        .ok()
                        .and_then(|j| j["response"]["player_level"].as_u64())
                        .map(|n| n as u32);
                    (id.clone(), level)
                })
            })
            .collect();
        handles.into_iter().filter_map(|h| h.join().ok()).collect()
    });

    for (id, level) in results {
        if let Some(level) = level {
            out.entry(id).or_default().level = Some(level);
        }
    }
}

pub fn fetch_without_key(steamids: &[String]) -> Map {
    let mut out: Map = HashMap::new();
    for (i, id) in steamids.iter().enumerate() {
        if i > 0 {
            std::thread::sleep(XML_THROTTLE);
        }
        let url = format!("https://steamcommunity.com/profiles/{id}/?xml=1");
        let Ok(body) = ureq::get(&url)
            .timeout(TIMEOUT)
            .call()
            .map_err(http_error)
            .and_then(|r| r.into_string().map_err(|e| e.to_string()))
        else {
            continue;
        };

        let mut intel = AccountIntel {
            persona_name: tag_text(&body, "steamID"),
            ..Default::default()
        };
        match tag_text(&body, "onlineState").as_deref() {
            Some("in-game") => {
                intel.persona_state = Some(1);
                intel.game = tag_text(&body, "gameName").or_else(|| Some("a game".into()));
            }
            Some("online") => intel.persona_state = Some(1),
            Some("offline") => intel.persona_state = Some(0),
            _ => {}
        }
        out.insert(id.clone(), intel);
    }
    out
}

fn tag_text(xml: &str, tag: &str) -> Option<String> {
    let open = format!("<{tag}>");
    let close = format!("</{tag}>");
    let start = xml.find(&open)? + open.len();
    let end = xml[start..].find(&close)? + start;
    let raw = xml[start..end].trim();
    let inner = raw
        .strip_prefix("<![CDATA[")
        .and_then(|r| r.strip_suffix("]]>"))
        .unwrap_or(raw)
        .trim();
    if inner.is_empty() {
        None
    } else {
        Some(inner.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_plain_and_cdata_tags() {
        let xml = "<profile><steamID><![CDATA[archie]]></steamID><onlineState>in-game</onlineState></profile>";
        assert_eq!(tag_text(xml, "steamID").as_deref(), Some("archie"));
        assert_eq!(tag_text(xml, "onlineState").as_deref(), Some("in-game"));
        assert_eq!(tag_text(xml, "missing"), None);
    }

    #[test]
    fn empty_tag_is_none() {
        assert_eq!(tag_text("<a></a>", "a"), None);
        assert_eq!(tag_text("<a><![CDATA[]]></a>", "a"), None);
    }

    #[test]
    fn banned_flag_follows_any_ban() {
        let mut i = AccountIntel::default();
        i.recompute_banned();
        assert!(!i.banned);

        i.game_bans = 1;
        i.recompute_banned();
        assert!(i.banned);

        let mut t = AccountIntel {
            trade_ban: Some("probation".into()),
            ..Default::default()
        };
        t.recompute_banned();
        assert!(t.banned);

        let mut n = AccountIntel {
            trade_ban: Some("none".into()),
            ..Default::default()
        };
        n.recompute_banned();
        assert!(!n.banned);
    }

    #[test]
    fn obviously_malformed_keys_are_rejected_offline() {
        assert_eq!(validate_key(""), Ok(false));
        assert_eq!(validate_key("not-a-key"), Ok(false));
        assert_eq!(validate_key(&"z".repeat(32)), Ok(false));
    }
}
