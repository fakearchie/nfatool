use std::fs;
use std::path::{Path, PathBuf};

use crate::settings::AppSettings;

use super::crypto::{compute_crc32, steam_encrypt};
use super::paths::{local_steam_cache_path, localconfig_path, steamid64_to_steamid3};
use super::vdf::{
    byte_offset_of_line, find_vdf_key_block_body_start, line_indent, quoted_fields,
    replace_vdf_key_line, validate_vdf_value,
};

pub(crate) fn check_steam_config_files(config_dir: &Path) -> Result<(), String> {
    let config_vdf = config_dir.join("config.vdf");
    let loginusers_vdf = config_dir.join("loginusers.vdf");
    if !config_vdf.exists() || !loginusers_vdf.exists() {
        return Err("Please open Steam and login to any account first to create the necessary config files.".into());
    }
    Ok(())
}

pub(crate) fn write_account_files(
    username: &str,
    jwt: &str,
    steamid: &str,
    steam_path: &Path,
) -> Result<(), String> {
    validate_vdf_value("Username", username)?;
    let config_vdf = steam_path.join("config").join("config.vdf");
    inject_account_into_config(&config_vdf, username, steamid)?;
    write_local_vdf(username, jwt)?;
    Ok(())
}

fn inject_account_into_config(path: &Path, username: &str, steamid: &str) -> Result<(), String> {
    let mut content = fs::read_to_string(path).map_err(|_| "Failed to read config.vdf")?;

    if config_contains_steamid(&content, steamid) {
        return Ok(());
    }

    let entry = format_account_config_entry(username, steamid);

    if let Some(insert_pos) = find_accounts_insert_pos(&content) {
        content.insert_str(insert_pos, &entry);
    } else if let Some((insert_pos, section)) =
        build_new_accounts_section(&content, username, steamid)?
    {
        content.insert_str(insert_pos, &section);
    } else {
        return Err(
            "Could not update config.vdf — open Steam once so it creates a valid config file."
                .to_string(),
        );
    }

    fs::write(path, content).map_err(|_| "Failed to write config.vdf")?;
    Ok(())
}

fn config_contains_steamid(content: &str, steamid: &str) -> bool {
    content.contains(&format!("\"SteamID\"\t\t\"{steamid}\""))
        || content.contains(&format!("\"SteamID\"\t\"{steamid}\""))
        || content.contains(&format!("\"SteamID\" \"{steamid}\""))
}

fn format_account_config_entry(username: &str, steamid: &str) -> String {
    format!(
        "\n\t\t\t\t\t\"{username}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"SteamID\"\t\t\"{steamid}\"\n\t\t\t\t\t}}\n"
    )
}

fn find_accounts_insert_pos(content: &str) -> Option<usize> {
    find_vdf_key_block_body_start(content, "Accounts")
}

fn build_new_accounts_section(
    content: &str,
    username: &str,
    steamid: &str,
) -> Result<Option<(usize, String)>, String> {
    let lines: Vec<&str> = content.lines().collect();
    let Some(close_idx) = find_steam_block_close_line(&lines) else {
        return Ok(None);
    };

    let open_idx = find_steam_block_open_line(&lines).ok_or("Steam block is malformed.")?;
    let key_indent = if close_idx > open_idx + 1 {
        line_indent(lines[close_idx - 1])
    } else {
        let close_indent = line_indent(lines[close_idx]);
        if close_indent.is_empty() {
            "\t\t\t".to_string()
        } else {
            format!("{close_indent}\t")
        }
    };

    let entry_indent = format!("{key_indent}\t");
    let entry = format!(
        "{entry_indent}\"{username}\"\n{entry_indent}{{\n{entry_indent}\t\"SteamID\"\t\t\"{steamid}\"\n{entry_indent}}}\n"
    );
    let section = format!("{key_indent}\"Accounts\"\n{key_indent}{{\n{entry}{key_indent}}}\n");
    let insert_pos = byte_offset_of_line(&lines, close_idx);
    Ok(Some((insert_pos, section)))
}

fn find_steam_block_open_line(lines: &[&str]) -> Option<usize> {
    for (i, line) in lines.iter().enumerate() {
        let fields = quoted_fields(line);
        if fields.len() == 1 && fields[0].eq_ignore_ascii_case("steam") {
            let mut j = i + 1;
            while j < lines.len() && lines[j].trim().is_empty() {
                j += 1;
            }
            if j < lines.len() && lines[j].trim() == "{" {
                return Some(j);
            }
        }
    }
    None
}

fn find_steam_block_close_line(lines: &[&str]) -> Option<usize> {
    let open = find_steam_block_open_line(lines)?;
    let mut depth = 0;
    for (i, line) in lines.iter().enumerate().skip(open) {
        let trimmed = line.trim();
        depth += trimmed.matches('{').count() as i32;
        depth -= trimmed.matches('}').count() as i32;
        if i > open && depth == 0 {
            return Some(i);
        }
    }
    None
}

pub(crate) fn remove_config_account(path: &Path, steamid: &str) -> Result<(), String> {
    let content = fs::read_to_string(path).map_err(|_| "Failed to read config.vdf")?;
    let new_content = remove_block_containing_steamid(&content, steamid);
    fs::write(path, new_content).map_err(|_| "Failed to write config.vdf".to_string())
}

fn remove_block_containing_steamid(content: &str, steamid: &str) -> String {
    let lines: Vec<&str> = content.lines().collect();
    let mut output = String::new();
    let mut i = 0;

    let accounts_range = find_accounts_block_range(&lines);

    while i < lines.len() {
        if let Some((acc_open, acc_close)) = accounts_range {
            if i > acc_open && i < acc_close {
                let fields = quoted_fields(lines[i]);
                if fields.len() == 1 {
                    let key_line = i;
                    let mut j = i + 1;
                    while j < lines.len() && lines[j].trim().is_empty() {
                        j += 1;
                    }
                    if j < lines.len() && lines[j].trim() == "{" {
                        let mut depth = 0_i32;
                        let mut end = j;
                        while end < lines.len() {
                            let t = lines[end].trim();
                            depth += t.matches('{').count() as i32;
                            depth -= t.matches('}').count() as i32;
                            end += 1;
                            if end > j && depth == 0 {
                                break;
                            }
                        }
                        let block = lines[key_line..end].join("\n");
                        if block.contains(&format!("\"SteamID\"\t\t\"{steamid}\""))
                            || block.contains(&format!("\"SteamID\"\t\"{steamid}\""))
                            || block.contains(&format!("\"SteamID\" \"{steamid}\""))
                        {
                            i = end;
                            continue;
                        }
                    }
                }
            }
        }

        output.push_str(lines[i]);
        output.push('\n');
        i += 1;
    }

    output
}

fn find_accounts_block_range(lines: &[&str]) -> Option<(usize, usize)> {
    for (i, line) in lines.iter().enumerate() {
        let fields = quoted_fields(line);
        if fields.len() == 1 && fields[0].eq_ignore_ascii_case("Accounts") {
            let mut j = i + 1;
            while j < lines.len() && lines[j].trim().is_empty() {
                j += 1;
            }
            if j < lines.len() && lines[j].trim() == "{" {
                let open = j;
                let mut depth = 0_i32;
                let mut k = open;
                while k < lines.len() {
                    let t = lines[k].trim();
                    depth += t.matches('{').count() as i32;
                    depth -= t.matches('}').count() as i32;
                    k += 1;
                    if k > open && depth == 0 {
                        return Some((open, k - 1));
                    }
                }
            }
        }
    }
    None
}

pub(crate) fn update_loginusers_vdf(
    path: &Path,
    username: &str,
    steamid: &str,
) -> Result<(), String> {
    let mut content = fs::read_to_string(path).map_err(|_| "Failed to read loginusers.vdf")?;
    content = content.replace("\"MostRecent\"\t\t\"1\"", "\"MostRecent\"\t\t\"0\"");
    content = content.replace("\"RememberPassword\"\t\t\"0\"", "\"RememberPassword\"\t\t\"1\"");
    content = content.replace("\"AllowAutoLogin\"\t\t\"0\"", "\"AllowAutoLogin\"\t\t\"1\"");

    if content.contains(&format!("\"{steamid}\"")) {
        content = update_existing_user(&content, username, steamid)?;
    } else {
        content = insert_new_user(&content, username, steamid)?;
    }
    fs::write(path, content).map_err(|_| "Failed to write loginusers.vdf")?;
    Ok(())
}

fn update_existing_user(content: &str, username: &str, steamid: &str) -> Result<String, String> {
    let mut result = String::new();
    let mut lines = content.lines();
    while let Some(line) = lines.next() {
        result.push_str(line);
        result.push('\n');
        if line.contains(&format!("\"{steamid}\"")) {
            let mut seen_remember = false;
            let mut seen_autologin = false;
            let mut seen_wants_offline = false;
            let mut seen_skip_offline = false;
            let mut block_lines: Vec<String> = Vec::new();

            for inner in lines.by_ref() {
                let done = inner.trim() == "}";
                if inner.contains("\"AccountName\"") {
                    block_lines.push(format!("\t\t\t\"AccountName\"\t\t\"{username}\"\n"));
                } else if inner.contains("\"PersonaName\"") {
                    block_lines.push(format!("\t\t\t\"PersonaName\"\t\t\"{username}\"\n"));
                } else if inner.contains("\"MostRecent\"") {
                    block_lines.push("\t\t\t\"MostRecent\"\t\t\"1\"\n".to_string());
                } else if inner.contains("\"Timestamp\"") {
                    block_lines.push(format!(
                        "\t\t\t\"Timestamp\"\t\t\"{}\"\n",
                        current_timestamp()
                    ));
                } else if inner.contains("\"RememberPassword\"") {
                    seen_remember = true;
                    block_lines.push("\t\t\t\"RememberPassword\"\t\t\"1\"\n".to_string());
                } else if inner.contains("\"AllowAutoLogin\"") {
                    seen_autologin = true;
                    block_lines.push("\t\t\t\"AllowAutoLogin\"\t\t\"1\"\n".to_string());
                } else if inner.contains("\"WantsOfflineMode\"") {
                    seen_wants_offline = true;
                    block_lines.push(inner.to_string() + "\n");
                } else if inner.contains("\"SkipOfflineModeWarning\"") {
                    seen_skip_offline = true;
                    block_lines.push(inner.to_string() + "\n");
                } else {
                    block_lines.push(inner.to_string() + "\n");
                }
                if done {
                    break;
                }
            }

            if !block_lines.is_empty() {
                let last = block_lines.pop().unwrap();
                if !seen_remember {
                    block_lines.push("\t\t\t\"RememberPassword\"\t\t\"1\"\n".to_string());
                }
                if !seen_autologin {
                    block_lines.push("\t\t\t\"AllowAutoLogin\"\t\t\"1\"\n".to_string());
                }
                if !seen_wants_offline {
                    block_lines.push("\t\t\t\"WantsOfflineMode\"\t\t\"0\"\n".to_string());
                }
                if !seen_skip_offline {
                    block_lines.push("\t\t\t\"SkipOfflineModeWarning\"\t\t\"0\"\n".to_string());
                }
                block_lines.push(last);
            }

            for bl in block_lines {
                result.push_str(&bl);
            }
        }
    }
    Ok(result)
}

fn insert_new_user(content: &str, username: &str, steamid: &str) -> Result<String, String> {
    let timestamp = current_timestamp();
    let insert_block = format!(
        r#"
	"{steamid}"
	{{
		"AccountName"		"{username}"
		"PersonaName"		"{username}"
		"RememberPassword"		"1"
		"WantsOfflineMode"		"0"
		"SkipOfflineModeWarning"		"0"
		"AllowAutoLogin"		"1"
		"MostRecent"		"1"
		"Timestamp"		"{timestamp}"
	}}
"#
    );
    let pos = content.rfind('}').ok_or("Invalid loginusers.vdf format")?;
    let mut new_content = content.to_string();
    new_content.insert_str(pos, &insert_block);
    Ok(new_content)
}

pub(crate) fn remove_loginuser(path: &Path, steamid: &str) -> Result<(), String> {
    let content = fs::read_to_string(path).map_err(|_| "Failed to read loginusers.vdf")?;
    let new_content = remove_steamid_block(&content, steamid)?;
    fs::write(path, new_content).map_err(|_| "Failed to write loginusers.vdf".to_string())
}

fn remove_steamid_block(content: &str, steamid: &str) -> Result<String, String> {
    let lines: Vec<&str> = content.lines().collect();
    let mut output = String::new();
    let mut removed = false;
    let mut i = 0;

    while i < lines.len() {
        let fields = quoted_fields(lines[i]);
        if fields.len() == 1 && fields[0] == steamid {
            let mut j = i + 1;
            while j < lines.len() && lines[j].trim().is_empty() {
                j += 1;
            }
            if j < lines.len() && lines[j].trim() == "{" {
                let mut depth = 0_i32;
                while i < lines.len() {
                    let trimmed = lines[i].trim();
                    depth += trimmed.matches('{').count() as i32;
                    depth -= trimmed.matches('}').count() as i32;
                    i += 1;
                    if i > j && depth == 0 {
                        break;
                    }
                }
                removed = true;
                continue;
            }
        }

        output.push_str(lines[i]);
        output.push('\n');
        i += 1;
    }

    if removed {
        Ok(output)
    } else {
        Err("Account was not found in loginusers.vdf.".to_string())
    }
}

pub(crate) fn remove_connect_cache_entry(content: &str, crc: &str) -> String {
    content
        .lines()
        .filter(|line| {
            let fields = quoted_fields(line);
            fields.is_empty() || fields[0] != crc
        })
        .map(|line| format!("{line}\n"))
        .collect()
}

fn write_local_vdf(username: &str, token: &str) -> Result<(), String> {
    let crc = compute_crc32(username);
    let encrypted = steam_encrypt(token, username)?;
    let base_path = local_steam_cache_path()?;
    let path = base_path.join("local.vdf");
    fs::create_dir_all(&base_path).map_err(|_| "Failed to create Steam directory")?;
    let content = if path.exists() {
        let existing = fs::read_to_string(&path).map_err(|_| "Failed to read local.vdf")?;
        inject_connect_cache(&existing, &crc, &encrypted)
            .unwrap_or_else(|_| create_new_local_vdf(&crc, &encrypted))
    } else {
        create_new_local_vdf(&crc, &encrypted)
    };
    fs::write(path, content).map_err(|_| "Failed to write local.vdf")?;
    Ok(())
}

fn inject_connect_cache(content: &str, crc: &str, encrypted: &str) -> Result<String, String> {
    let mut output = String::new();
    let mut in_connect_cache = false;
    let mut brace_depth = 0;
    let mut replaced = false;

    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed == "\"ConnectCache\"" {
            in_connect_cache = true;
            brace_depth = 0;
            output.push_str(line);
            output.push('\n');
            continue;
        }

        if in_connect_cache {
            if trimmed.starts_with('{') {
                brace_depth += 1;
            } else if trimmed.starts_with('}') {
                brace_depth -= 1;
                if brace_depth == 0 && !replaced {
                    output.push_str(&format!("\t\t\t\t\t\"{crc}\"\t\t\"{encrypted}\"\n"));
                    replaced = true;
                }
            }

            if trimmed.starts_with(&format!("\"{crc}\"")) {
                output.push_str(&format!("\t\t\t\t\t\"{crc}\"\t\t\"{encrypted}\"\n"));
                replaced = true;
                continue;
            }
        }

        output.push_str(line);
        output.push('\n');
    }

    if !replaced {
        return Err("ConnectCache block not found".into());
    }
    Ok(output)
}

fn create_new_local_vdf(crc: &str, encrypted: &str) -> String {
    format!(
        r#""MachineUserConfigStore"
{{
	"Software"
	{{
		"Valve"
		{{
			"Steam"
			{{
				"ConnectCache"
				{{
					"{crc}"		"{encrypted}"
				}}
			}}
		}}
	}}
}}
"#
    )
}

pub(crate) fn apply_localconfig_settings(
    steamid64: &str,
    steam_path: &Path,
    settings: &AppSettings,
) -> Result<PathBuf, String> {
    let steamid3 = steamid64_to_steamid3(steamid64)?;
    let path = localconfig_path(steam_path, &steamid3);
    let persona_state = if settings.always_invisible { 7 } else { 1 };

    let mut content = fs::read_to_string(&path)
        .unwrap_or_else(|_| minimal_localconfig_template(&steamid3, persona_state));

    content = patch_persona_prefs(&content, &steamid3, persona_state);
    content = replace_vdf_key_line(&content, "SignIntoFriends", "1");

    if settings.mute_notifications_on_login {
        content = patch_friends_notifications(&content);
    }

    let parent = path
        .parent()
        .ok_or("Failed to resolve userdata config directory")?;
    fs::create_dir_all(parent).map_err(|_| "Failed to create userdata config directory")?;
    fs::write(&path, content).map_err(|_| "Failed to write localconfig.vdf")?;
    Ok(path)
}

fn minimal_localconfig_template(steamid3: &str, persona_state: u8) -> String {
    format!(
        r#""UserLocalConfigStore"
{{
	"friends"
	{{
		"SignIntoFriends"		"1"
	}}
	"WebStorage"
	{{
		"FriendStoreLocalPrefs_{steamid3}"		"{{\"ePersonaState\":{persona_state},\"strNonFriendsAllowedToMsg\":\"\"}}"
	}}
}}
"#
    )
}

fn patch_persona_prefs(content: &str, steamid3: &str, persona_state: u8) -> String {
    let key = format!("FriendStoreLocalPrefs_{steamid3}");
    let new_json =
        format!(r#"{{\"ePersonaState\":{persona_state},\"strNonFriendsAllowedToMsg\":\"\"}}"#);

    if content.contains(&key) {
        let mut out = String::new();
        for line in content.lines() {
            if line.contains(&key) {
                let indent = line_indent(line);
                out.push_str(&format!("{indent}\"{key}\"\t\t\"{new_json}\"\n"));
            } else {
                out.push_str(line);
                out.push('\n');
            }
        }
        out
    } else if let Some(insert_pos) = find_vdf_key_block_body_start(content, "WebStorage") {
        let entry = format!("\t\t\"{key}\"\t\t\"{new_json}\"\n");
        let mut patched = content.to_string();
        patched.insert_str(insert_pos, &entry);
        patched
    } else {
        let mut patched = content.to_string();
        if let Some(pos) = patched.rfind('}') {
            let block = format!("\t\"WebStorage\"\n\t{{\n\t\t\"{key}\"\t\t\"{new_json}\"\n\t}}\n");
            patched.insert_str(pos, &block);
        }
        patched
    }
}

fn patch_friends_notifications(content: &str) -> String {
    let keys = [
        "Notifications_ShowOnline",
        "Notifications_ShowMessage",
        "Notifications_ShowIngame",
        "Notifications_EventsAndAnnouncements",
        "Sounds_PlayOnline",
        "Sounds_PlayMessage",
        "Sounds_PlayIngame",
        "Sounds_EventsAndAnnouncements",
    ];
    let mut patched = content.to_string();
    if find_vdf_key_block_body_start(&patched, "friends").is_none() {
        if let Some(pos) = patched.rfind('}') {
            patched.insert_str(
                pos,
                "\t\"friends\"\n\t{\n\t\t\"SignIntoFriends\"\t\t\"1\"\n\t}\n",
            );
        }
    }
    for key in keys {
        patched = replace_vdf_key_line(&patched, key, "0");
    }
    patched
}

// Clear only the cached login tokens (%LOCALAPPDATA%\Steam\local.vdf ConnectCache),
// which signs Steam out on this PC. Deliberately does NOT touch config.vdf /
// loginusers.vdf or delete the rest of the Steam local cache (htmlcache, logs,
// depotcache, …) — the old "delete the whole folder" behaviour was destructive and
// left saved accounts unrecoverable.
pub(crate) fn clear_login_cache(steam_base_dir: &Path) -> Result<(), String> {
    let local_vdf = steam_base_dir.join("local.vdf");
    if local_vdf.exists() {
        fs::remove_file(&local_vdf).map_err(|e| format!("Failed to clear login cache: {e}"))?;
    }
    Ok(())
}

fn current_timestamp() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs()
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inject_without_existing_accounts_block() {
        let config = r#""InstallConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"MTBF"		"123"
			}
		}
	}
}
"#;
        let dir = std::env::temp_dir().join(format!("nfatool_test_{}", std::process::id()));
        let _ = fs::create_dir_all(&dir);
        let path = dir.join("config.vdf");
        fs::write(&path, config).unwrap();
        inject_account_into_config(&path, "76561199843081825", "76561199843081825").unwrap();
        let updated = fs::read_to_string(&path).unwrap();
        assert!(updated.to_lowercase().contains("\"accounts\""));
        assert!(config_contains_steamid(&updated, "76561199843081825"));
        let _ = fs::remove_dir_all(&dir);
    }
}
