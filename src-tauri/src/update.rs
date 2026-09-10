
use serde::Serialize;

const RELEASES_API: &str = "https://api.github.com/repos/fakearchie/nfatool/releases/latest";
const RELEASES_PAGE: &str = "https://github.com/fakearchie/nfatool/releases/latest";

#[derive(Serialize, Default)]
pub struct UpdateInfo {
    pub available: bool,
    pub current: String,
    pub latest: String,
    pub notes: String,
    pub url: String,
}

fn is_newer(latest: &str, current: &str) -> bool {
    let parse = |v: &str| -> Vec<u64> {
        v.trim_start_matches('v')
            .split(['.', '-', '+'])
            .map(|part| part.parse::<u64>().unwrap_or(0))
            .collect()
    };
    let (a, b) = (parse(latest), parse(current));
    for i in 0..a.len().max(b.len()) {
        let (x, y) = (a.get(i).copied().unwrap_or(0), b.get(i).copied().unwrap_or(0));
        if x != y {
            return x > y;
        }
    }
    false
}

pub fn check(current: &str) -> Result<UpdateInfo, String> {
    let response = ureq::get(RELEASES_API)
        .set("User-Agent", "nfa.pub-tool")
        .set("Accept", "application/vnd.github+json")
        .timeout(std::time::Duration::from_secs(10))
        .call()
        .map_err(|e| format!("Could not reach GitHub: {e}"))?;

    let json: serde_json::Value = response
        .into_json()
        .map_err(|_| "GitHub sent something unexpected.".to_string())?;

    let latest = json
        .get("tag_name")
        .and_then(|v| v.as_str())
        .unwrap_or_default()
        .trim_start_matches('v')
        .to_string();

    if latest.is_empty() {
        return Err("No published release found.".to_string());
    }

    let notes = json
        .get("body")
        .and_then(|v| v.as_str())
        .unwrap_or_default()
        .trim()
        .to_string();

    Ok(UpdateInfo {
        available: is_newer(&latest, current),
        current: current.to_string(),
        latest,
        notes,
        url: json
            .get("html_url")
            .and_then(|v| v.as_str())
            .unwrap_or(RELEASES_PAGE)
            .to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compares_numerically_not_lexically() {
        assert!(is_newer("0.10.0", "0.9.0"));
        assert!(!is_newer("0.9.0", "0.10.0"));
    }

    #[test]
    fn equal_versions_are_not_newer() {
        assert!(!is_newer("0.3.0", "0.3.0"));
        assert!(!is_newer("v0.3.0", "0.3.0"));
        assert!(!is_newer("0.3.0", "v0.3.0"));
    }

    #[test]
    fn handles_shorter_and_longer_versions() {
        assert!(is_newer("0.3.1", "0.3"));
        assert!(!is_newer("0.3", "0.3.1"));
        assert!(!is_newer("0.3.0", "0.3"));
    }

    #[test]
    fn older_releases_are_never_offered() {
        assert!(!is_newer("0.2.9", "0.3.0"));
        assert!(is_newer("1.0.0", "0.99.99"));
    }

    #[test]
    fn junk_does_not_panic_or_offer_an_update() {
        assert!(!is_newer("", "0.3.0"));
        assert!(!is_newer("not-a-version", "0.3.0"));
    }
}
