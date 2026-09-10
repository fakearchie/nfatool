use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

pub struct FileMemo(Mutex<BTreeMap<PathBuf, (SystemTime, Option<String>)>>);

impl FileMemo {
    pub const fn new() -> Self {
        Self(Mutex::new(BTreeMap::new()))
    }

    // Keyed on mtime, so an avatar Steam replaces is picked up on the next read
    // while an unchanged one costs a stat() instead of a read + base64.
    pub fn get_or(&self, path: &Path, compute: impl FnOnce() -> Option<String>) -> Option<String> {
        let stamp = std::fs::metadata(path).ok()?.modified().ok()?;

        if let Ok(map) = self.0.lock() {
            if let Some((cached, value)) = map.get(path) {
                if *cached == stamp {
                    return value.clone();
                }
            }
        }

        let value = compute();
        if let Ok(mut map) = self.0.lock() {
            map.insert(path.to_path_buf(), (stamp, value.clone()));
        }
        value
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    #[test]
    fn recomputes_only_when_the_file_changes() {
        let dir = std::env::temp_dir().join("nfatool-memo-test");
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join("a.txt");
        std::fs::write(&file, "one").unwrap();

        static MEMO: FileMemo = FileMemo::new();
        static CALLS: AtomicU32 = AtomicU32::new(0);
        let compute = || {
            CALLS.fetch_add(1, Ordering::SeqCst);
            Some(std::fs::read_to_string(&file).unwrap())
        };

        assert_eq!(MEMO.get_or(&file, compute).as_deref(), Some("one"));
        assert_eq!(MEMO.get_or(&file, compute).as_deref(), Some("one"));
        assert_eq!(CALLS.load(Ordering::SeqCst), 1, "second read must hit the cache");

        // A new mtime must invalidate. Filesystem stamps are coarse, so set it.
        std::fs::write(&file, "two").unwrap();
        let later = SystemTime::now() + std::time::Duration::from_secs(2);
        let f = std::fs::OpenOptions::new().write(true).open(&file).unwrap();
        f.set_modified(later).unwrap();
        drop(f);

        assert_eq!(MEMO.get_or(&file, compute).as_deref(), Some("two"));
        assert_eq!(CALLS.load(Ordering::SeqCst), 2);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_missing_file_is_none_and_is_not_cached() {
        static MEMO: FileMemo = FileMemo::new();
        let missing = std::env::temp_dir().join("nfatool-memo-does-not-exist");
        assert_eq!(MEMO.get_or(&missing, || Some("x".into())), None);
    }
}
