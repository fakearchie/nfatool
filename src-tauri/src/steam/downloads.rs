// Pause background game downloads at sign-in.
//
// Steam has no persistent "pause all downloads" flag on disk — the client's
// Pause/Resume toggle is runtime-only, and on login Steam rebuilds the queue from
// the per-app manifests and auto-resumes anything that needs content. The only
// reliable, non-corrupting, reversible lever is the per-game AutoUpdateBehavior
// setting (Steam's own "Only update this game when I launch it"). Setting it to 1
// stops Steam from auto-starting that game's update download in the background;
// the game still updates when the user launches it.
//
// (The previous approach wrote AllowDownloadsDuringGameplay into localconfig.vdf,
// which is both the wrong key — it only governs downloading *while in a game* —
// and the wrong file, so it never did anything.)
//
// Must run while Steam is closed (the sign-in flow already kills it). Best-effort:
// a manifest we cannot parse is skipped, never fatal to the login.

use std::fs;
use std::path::{Path, PathBuf};

use super::vdf::{quoted_fields, replace_vdf_key_line};

/// Defer automatic updates for every installed game across every library.
/// Returns how many manifests were changed. Only games currently set to
/// "always keep updated" (AutoUpdateBehavior 0) are touched; games the user set
/// to "on launch" (1) or "high priority" (2) are left alone.
pub(crate) fn defer_game_updates(steam_path: &Path) -> usize {
    let mut changed = 0;
    for library in library_roots(steam_path) {
        let Ok(entries) = fs::read_dir(library.join("steamapps")) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if is_appmanifest(&path) && defer_manifest(&path) {
                changed += 1;
            }
        }
    }
    changed
}

fn is_appmanifest(path: &Path) -> bool {
    path.file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.starts_with("appmanifest_") && n.ends_with(".acf"))
}

/// Returns true if the manifest was changed and written.
fn defer_manifest(path: &Path) -> bool {
    let Ok(content) = fs::read_to_string(path) else {
        return false;
    };
    let updated = defer_manifest_content(&content);
    match updated {
        Some(new_content) => fs::write(path, new_content).is_ok(),
        None => false,
    }
}

/// Pure transform: given manifest text, returns the deferred version, or None if
/// the game is not on auto-update (nothing to change).
fn defer_manifest_content(content: &str) -> Option<String> {
    if current_value(content, "AutoUpdateBehavior").as_deref() != Some("0") {
        return None;
    }
    let updated = replace_vdf_key_line(content, "AutoUpdateBehavior", "1");
    let updated = replace_vdf_key_line(&updated, "ScheduledAutoUpdate", "0");
    (updated != content).then_some(updated)
}

fn current_value(content: &str, key: &str) -> Option<String> {
    content.lines().find_map(|line| {
        let fields = quoted_fields(line);
        (fields.len() >= 2 && fields[0] == key).then(|| fields[1].clone())
    })
}

/// The install dir plus every `"path"` listed in libraryfolders.vdf.
fn library_roots(steam_path: &Path) -> Vec<PathBuf> {
    let mut roots = vec![steam_path.to_path_buf()];
    let libraryfolders = steam_path.join("steamapps").join("libraryfolders.vdf");
    if let Ok(content) = fs::read_to_string(&libraryfolders) {
        for line in content.lines() {
            let fields = quoted_fields(line);
            if fields.len() >= 2 && fields[0] == "path" {
                // VDF escapes backslashes; unescape to a real Windows path.
                let root = PathBuf::from(fields[1].replace("\\\\", "\\"));
                if !roots.contains(&root) {
                    roots.push(root);
                }
            }
        }
    }
    roots
}

#[cfg(test)]
mod tests {
    use super::*;

    const MANIFEST: &str = "\"AppState\"\n{\n\t\"appid\"\t\t\"440\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"AutoUpdateBehavior\"\t\t\"0\"\n\t\"ScheduledAutoUpdate\"\t\t\"1717388325\"\n}\n";

    #[test]
    fn defers_auto_update_game() {
        let out = defer_manifest_content(MANIFEST).expect("should change");
        assert!(out.contains("\"AutoUpdateBehavior\"\t\t\"1\""));
        assert!(out.contains("\"ScheduledAutoUpdate\"\t\t\"0\""));
        // StateFlags and other bytes must be left untouched.
        assert!(out.contains("\"StateFlags\"\t\t\"4\""));
        assert!(out.contains("\"appid\"\t\t\"440\""));
    }

    #[test]
    fn leaves_on_launch_game_untouched() {
        let manifest = MANIFEST.replace("\"AutoUpdateBehavior\"\t\t\"0\"", "\"AutoUpdateBehavior\"\t\t\"1\"");
        assert!(defer_manifest_content(&manifest).is_none());
    }

    #[test]
    fn leaves_high_priority_game_untouched() {
        let manifest = MANIFEST.replace("\"AutoUpdateBehavior\"\t\t\"0\"", "\"AutoUpdateBehavior\"\t\t\"2\"");
        assert!(defer_manifest_content(&manifest).is_none());
    }

    #[test]
    fn extracts_library_paths() {
        // Smoke test the unescape logic used for libraryfolders.vdf values.
        let raw = "D:\\\\SteamLibrary";
        assert_eq!(raw.replace("\\\\", "\\"), "D:\\SteamLibrary");
    }
}
