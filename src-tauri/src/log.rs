// A small in-memory log for the things that fail quietly.
//
// Most errors reach the user as a toast. The ones that don't are the best-effort
// steps we deliberately refuse to fail a sign-in over — CS2 file edits, the
// screen-capture flag, avatar fetches. In a windowed app `eprintln!` goes nowhere,
// so those failures were invisible to the user *and* to anyone helping them. This
// keeps the last few in memory for the Settings panel.
//
// Deliberately not written to disk: it can contain account names, and a log file
// nobody asked for is a small privacy liability that outlives the session.

use std::collections::VecDeque;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

const CAPACITY: usize = 60;

static ENTRIES: Mutex<VecDeque<String>> = Mutex::new(VecDeque::new());

fn stamp() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (h, m, s) = ((secs / 3600) % 24, (secs / 60) % 60, secs % 60);
    format!("{h:02}:{m:02}:{s:02}")
}

/// Records one line. Never panics and never blocks meaningfully — a poisoned lock
/// just drops the entry, because logging must not be able to break the thing it is
/// reporting on.
pub fn record(message: impl Into<String>) {
    let line = format!("{} {}", stamp(), message.into());
    eprintln!("{line}");
    if let Ok(mut entries) = ENTRIES.lock() {
        if entries.len() == CAPACITY {
            entries.pop_front();
        }
        entries.push_back(line);
    }
}

/// Newest first, which is the order anyone reading a log actually wants.
pub fn entries() -> Vec<String> {
    ENTRIES
        .lock()
        .map(|e| e.iter().rev().cloned().collect())
        .unwrap_or_default()
}

pub fn clear() {
    if let Ok(mut entries) = ENTRIES.lock() {
        entries.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One test for the whole module: these share global state, so splitting them
    /// would make them order-dependent.
    #[test]
    fn records_newest_first_and_stays_bounded() {
        clear();
        assert!(entries().is_empty());

        record("first");
        record("second");
        let got = entries();
        assert_eq!(got.len(), 2);
        assert!(got[0].ends_with("second"), "newest should be first: {got:?}");
        assert!(got[1].ends_with("first"));

        for i in 0..CAPACITY * 2 {
            record(format!("line {i}"));
        }
        let got = entries();
        assert_eq!(got.len(), CAPACITY, "the buffer must not grow without bound");
        // The oldest entries are gone, the newest survived.
        assert!(got[0].ends_with(&format!("line {}", CAPACITY * 2 - 1)));
        assert!(!got.iter().any(|l| l.ends_with("first")));

        clear();
        assert!(entries().is_empty());
    }

    #[test]
    fn every_line_is_timestamped() {
        let line = format!("{} x", stamp());
        assert_eq!(line.len(), "00:00:00 x".len());
        assert_eq!(line.matches(':').count(), 2);
    }
}
