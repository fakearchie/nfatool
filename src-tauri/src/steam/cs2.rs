// Counter-Strike 2 per-account tooling.
//
// Everything here edits files under Steam's `userdata/<steamid3>/` tree and must
// run while Steam is closed — the caller already stopped it. All of it is
// best-effort: none of these are worth failing a sign-in over, so the entry point
// collects problems and returns them for logging rather than propagating.

use std::fs;
use std::path::{Path, PathBuf};

use super::paths::{localconfig_path, steamid64_to_steamid3};
use super::vdf::set_nested_key;
use crate::settings::AppSettings;

pub(crate) const APPID: &str = "730";

/// `UserLocalConfigStore/Software/Valve/Steam/apps/730`, where per-game settings live.
const APP_PATH: [&str; 5] = ["Software", "Valve", "Steam", "apps", APPID];

fn userdata_dir(steam_path: &Path) -> PathBuf {
    steam_path.join("userdata")
}

fn game_config_dir(steam_path: &Path, steamid3: &str) -> PathBuf {
    userdata_dir(steam_path).join(steamid3).join(APPID)
}

/// Applies every CS2 preference that lives in `localconfig.vdf` in a single
/// read-modify-write, rather than reopening the file once per setting.
fn patch_localconfig(steam_path: &Path, steamid3: &str, settings: &AppSettings) -> Result<(), String> {
    let path = localconfig_path(steam_path, steamid3);
    let Ok(mut content) = fs::read_to_string(&path) else {
        // No localconfig yet means Steam has never signed this account in here;
        // `apply_localconfig_settings` creates it, and we run after that.
        return Ok(());
    };

    let mut changed = false;
    if !settings.cs2_launch_options.trim().is_empty() {
        content = set_nested_key(
            &content,
            &APP_PATH,
            "LaunchOptions",
            settings.cs2_launch_options.trim(),
        );
        changed = true;
    }
    if settings.disable_remote_play {
        content = set_nested_key(&content, &["streaming_v2"], "EnableStreaming", "0");
        changed = true;
    }

    if changed {
        fs::write(&path, content).map_err(|e| format!("localconfig.vdf: {e}"))?;
    }
    Ok(())
}

/// Marks every subscribed Workshop item as `disabled_locally`, so signing in does
/// not kick off a re-download of maps this account never asked for.
fn suppress_workshop(steam_path: &Path, steamid3: &str) -> Result<(), String> {
    let path = userdata_dir(steam_path)
        .join(steamid3)
        .join("ugc")
        .join(format!("{APPID}_subscriptions.vdf"));
    let Ok(content) = fs::read_to_string(&path) else {
        return Ok(()); // nothing subscribed
    };

    let mut out = String::new();
    let mut changed = false;
    for line in content.lines() {
        let fields = super::vdf::quoted_fields(line);
        if fields.len() >= 2 && fields[0] == "disabled_locally" && fields[1] != "1" {
            let indent = super::vdf::line_indent(line);
            out.push_str(&format!("{indent}\"disabled_locally\"\t\t\"1\"\n"));
            changed = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }

    if changed {
        fs::write(&path, out).map_err(|e| format!("{APPID}_subscriptions.vdf: {e}"))?;
    }
    Ok(())
}

/// Copies one account's CS2 settings tree (video config, cfg/, crosshairs) onto
/// another, so an alt inherits the setup instead of starting from Valve's defaults.
///
/// Skips silently when source and target are the same account, or when the source
/// has no CS2 data — neither is an error the user needs to hear about.
fn copy_game_config(steam_path: &Path, from3: &str, to3: &str) -> Result<u32, String> {
    if from3 == to3 {
        return Ok(0);
    }
    let source = game_config_dir(steam_path, from3);
    if !source.is_dir() {
        return Ok(0);
    }
    let target = game_config_dir(steam_path, to3);
    copy_dir(&source, &target)
}

fn copy_dir(from: &Path, to: &Path) -> Result<u32, String> {
    fs::create_dir_all(to).map_err(|e| format!("{}: {e}", to.display()))?;
    let entries = fs::read_dir(from).map_err(|e| format!("{}: {e}", from.display()))?;
    let mut copied = 0;
    for entry in entries.flatten() {
        let src = entry.path();
        let dst = to.join(entry.file_name());
        match entry.file_type() {
            Ok(t) if t.is_dir() => copied += copy_dir(&src, &dst)?,
            Ok(t) if t.is_file() => {
                fs::copy(&src, &dst).map_err(|e| format!("{}: {e}", src.display()))?;
                copied += 1;
            }
            // Symlinks and anything else are left alone rather than followed.
            _ => {}
        }
    }
    Ok(copied)
}

/// Runs the CS2 steps for the account being signed in. Returns the problems it hit
/// so the caller can log them; an empty vec means everything applied.
pub(crate) fn apply_on_login(
    steamid64: &str,
    steam_path: &Path,
    settings: &AppSettings,
) -> Vec<String> {
    let Ok(steamid3) = steamid64_to_steamid3(steamid64) else {
        return vec!["Could not derive the SteamID3 for CS2 settings.".to_string()];
    };

    let mut problems = Vec::new();
    if let Err(e) = patch_localconfig(steam_path, &steamid3, settings) {
        problems.push(e);
    }
    if settings.suppress_workshop_downloads {
        if let Err(e) = suppress_workshop(steam_path, &steamid3) {
            problems.push(e);
        }
    }

    let source = settings.cs2_config_source.trim();
    if !source.is_empty() {
        match steamid64_to_steamid3(source) {
            Ok(from3) => {
                if let Err(e) = copy_game_config(steam_path, &from3, &steamid3) {
                    problems.push(e);
                }
            }
            Err(e) => problems.push(format!("CS2 config source: {e}")),
        }
    }

    problems
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings_with(options: &str, remote: bool) -> AppSettings {
        AppSettings {
            cs2_launch_options: options.to_string(),
            disable_remote_play: remote,
            ..Default::default()
        }
    }

    #[test]
    fn launch_options_land_in_the_app_block() {
        let src = "\"UserLocalConfigStore\"\n{\n}\n";
        let out = set_nested_key(src, &APP_PATH, "LaunchOptions", "-novid -high");
        assert!(out.contains("\"730\""));
        assert!(out.contains("\"LaunchOptions\"\t\t\"-novid -high\""));
    }

    #[test]
    fn workshop_lines_are_flipped_and_others_kept() {
        let content = "\"730\"\n{\n\t\"3070563536\"\n\t{\n\t\t\"disabled_locally\"\t\t\"0\"\n\t\t\"timeupdated\"\t\t\"1717388325\"\n\t}\n}\n";
        let mut out = String::new();
        for line in content.lines() {
            let fields = super::super::vdf::quoted_fields(line);
            if fields.len() >= 2 && fields[0] == "disabled_locally" && fields[1] != "1" {
                out.push_str(&format!(
                    "{}\"disabled_locally\"\t\t\"1\"\n",
                    super::super::vdf::line_indent(line)
                ));
            } else {
                out.push_str(line);
                out.push('\n');
            }
        }
        assert!(out.contains("\"disabled_locally\"\t\t\"1\""));
        assert!(out.contains("\"timeupdated\"\t\t\"1717388325\""));
    }

    /// Copying an account onto itself would walk a directory while writing into it.
    #[test]
    fn copying_to_the_same_account_is_a_no_op() {
        let dir = std::env::temp_dir().join("nfatool-cs2-selfcopy");
        assert_eq!(copy_game_config(&dir, "123", "123").unwrap(), 0);
    }

    #[test]
    fn missing_source_is_not_an_error() {
        let dir = std::env::temp_dir().join("nfatool-cs2-missing-source");
        assert_eq!(copy_game_config(&dir, "111", "222").unwrap(), 0);
    }

    #[test]
    fn copies_a_nested_tree() {
        let root = std::env::temp_dir().join("nfatool-cs2-copytest");
        let _ = fs::remove_dir_all(&root);
        let src = game_config_dir(&root, "111").join("local").join("cfg");
        fs::create_dir_all(&src).unwrap();
        fs::write(src.join("autoexec.cfg"), "sensitivity 1.6").unwrap();
        fs::write(game_config_dir(&root, "111").join("top.txt"), "x").unwrap();

        assert_eq!(copy_game_config(&root, "111", "222").unwrap(), 2);
        let copied = game_config_dir(&root, "222").join("local").join("cfg").join("autoexec.cfg");
        assert_eq!(fs::read_to_string(copied).unwrap(), "sensitivity 1.6");
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn no_settings_means_no_write() {
        // With no launch options and remote play left alone there is nothing to do,
        // so a missing localconfig must not be treated as a failure.
        let dir = std::env::temp_dir().join("nfatool-cs2-nowrite");
        assert!(patch_localconfig(&dir, "123", &settings_with("", false)).is_ok());
    }
}
