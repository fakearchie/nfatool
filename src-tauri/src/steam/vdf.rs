pub(crate) fn quoted_fields(line: &str) -> Vec<String> {
    let mut fields = Vec::new();
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch != '"' {
            continue;
        }
        let mut value = String::new();
        for inner in chars.by_ref() {
            if inner == '"' {
                break;
            }
            value.push(inner);
        }
        fields.push(value);
    }
    fields
}

pub(crate) fn find_vdf_key_block_body_start(content: &str, key: &str) -> Option<usize> {
    let lines: Vec<&str> = content.lines().collect();
    for (i, line) in lines.iter().enumerate() {
        let fields = quoted_fields(line);
        if fields.len() == 1 && fields[0].eq_ignore_ascii_case(key) {
            let mut j = i + 1;
            while j < lines.len() && lines[j].trim().is_empty() {
                j += 1;
            }
            if j < lines.len() && lines[j].trim() == "{" {
                return Some(byte_offset_of_line(&lines, j + 1));
            }
        }
    }
    None
}

pub(crate) fn byte_offset_of_line(lines: &[&str], line_idx: usize) -> usize {
    lines
        .iter()
        .take(line_idx)
        .map(|line| line.len() + 1)
        .sum()
}

pub(crate) fn line_indent(line: &str) -> String {
    line.chars()
        .take_while(|c| *c == '\t' || *c == ' ')
        .collect()
}

pub(crate) fn replace_vdf_key_line(content: &str, key: &str, value: &str) -> String {
    let mut out = String::new();
    let mut replaced = false;

    for line in content.lines() {
        let fields = quoted_fields(line);
        if fields.len() >= 2 && fields[0] == key {
            let indent = line_indent(line);
            out.push_str(&format!("{indent}\"{key}\"\t\t\"{value}\"\n"));
            replaced = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }

    if !replaced {
        if let Some(pos) = content.rfind('}') {
            let mut patched = content.to_string();
            patched.insert_str(pos, &format!("\t\"{key}\"\t\t\"{value}\"\n"));
            return patched;
        }
    }

    out
}

/// `path` is relative to the file's single top-level block, so drop that frame.
fn inner_stack(stack: &[String]) -> &[String] {
    stack.get(1..).unwrap_or(&[])
}

fn is_prefix_of(stack: &[String], path: &[&str]) -> bool {
    stack.len() <= path.len() && stack.iter().zip(path).all(|(a, b)| a == b)
}

/// Sets `key` inside a nested block, creating whatever part of `path` is missing.
///
/// Steam writes section names and their opening brace on separate lines, so this
/// tracks a stack of names rather than trying to match a pattern. Everything it
/// does not touch is copied through verbatim — the file is full of settings we
/// have no business rewriting.
///
/// `path` is relative to the top-level block, e.g.
/// `["Software", "Valve", "Steam", "apps", "730"]` for `UserLocalConfigStore`.
pub(crate) fn set_nested_key(content: &str, path: &[&str], key: &str, value: &str) -> String {
    let mut stack: Vec<String> = Vec::new();
    let mut pending: Option<String> = None;
    let mut replaced = false;
    let mut out: Vec<String> = Vec::new();

    // The deepest prefix of `path` that already exists, and where its block ends —
    // that is where anything missing has to be grafted on.
    let mut best_depth = 0usize;
    let mut best_close: Option<usize> = None;

    for line in content.lines() {
        let trimmed = line.trim();

        if trimmed == "{" {
            stack.push(pending.take().unwrap_or_default());
            out.push(line.to_string());
            continue;
        }

        if trimmed.starts_with('}') {
            let inner = inner_stack(&stack);
            if is_prefix_of(inner, path) && (best_close.is_none() || inner.len() > best_depth) {
                best_depth = inner.len();
                best_close = Some(out.len());
            }
            stack.pop();
            pending = None;
            out.push(line.to_string());
            continue;
        }

        let fields = quoted_fields(trimmed);
        if fields.len() == 1 {
            pending = Some(fields[0].clone());
            out.push(line.to_string());
            continue;
        }
        pending = None;

        let inner = inner_stack(&stack);
        if fields.len() >= 2 && fields[0] == key && inner.len() == path.len() && is_prefix_of(inner, path)
        {
            out.push(format!("{}\"{key}\"\t\t\"{value}\"", line_indent(line)));
            replaced = true;
            continue;
        }
        out.push(line.to_string());
    }

    if !replaced {
        if let Some(idx) = best_close {
            let close_indent = line_indent(&out[idx]);
            let mut block: Vec<String> = Vec::new();
            let mut indent = format!("{close_indent}\t");
            for name in &path[best_depth..] {
                block.push(format!("{indent}\"{name}\""));
                block.push(format!("{indent}{{"));
                indent.push('\t');
            }
            block.push(format!("{indent}\"{key}\"\t\t\"{value}\""));
            for _ in &path[best_depth..] {
                indent.pop();
                block.push(format!("{indent}}}"));
            }
            out.splice(idx..idx, block);
        }
    }

    let mut joined = out.join("\n");
    joined.push('\n');
    joined
}

pub(crate) fn validate_vdf_value(label: &str, value: &str) -> Result<(), String> {
    if value.is_empty() {
        return Err(format!("{label} is missing."));
    }
    if value
        .chars()
        .any(|c| matches!(c, '"' | '\\' | '{' | '}' | '\n' | '\r' | '\t'))
    {
        return Err(format!(
            "{label} contains invalid characters. Copy the plain account name only."
        ));
    }
    Ok(())
}

#[cfg(test)]
mod nested_key_tests {
    use super::*;

    const APPS_PATH: [&str; 5] = ["Software", "Valve", "Steam", "apps", "730"];

    fn with_existing_key() -> String {
        [
            "\"UserLocalConfigStore\"",
            "{",
            "\t\"Software\"",
            "\t{",
            "\t\t\"Valve\"",
            "\t\t{",
            "\t\t\t\"Steam\"",
            "\t\t\t{",
            "\t\t\t\t\"apps\"",
            "\t\t\t\t{",
            "\t\t\t\t\t\"730\"",
            "\t\t\t\t\t{",
            "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-old\"",
            "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1717388325\"",
            "\t\t\t\t\t}",
            "\t\t\t\t}",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}",
        ]
        .join("\n")
            + "\n"
    }

    #[test]
    fn replaces_an_existing_value() {
        let out = set_nested_key(&with_existing_key(), &APPS_PATH, "LaunchOptions", "-novid");
        assert!(out.contains("\"LaunchOptions\"\t\t\"-novid\""));
        assert!(!out.contains("-old"));
        // Siblings must survive untouched.
        assert!(out.contains("\"LastPlayed\"\t\t\"1717388325\""));
    }

    #[test]
    fn adds_the_key_to_an_existing_block() {
        let src = with_existing_key().replace("\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-old\"\n", "");
        let out = set_nested_key(&src, &APPS_PATH, "LaunchOptions", "-novid");
        assert!(out.contains("\"LaunchOptions\"\t\t\"-novid\""));
        assert!(out.contains("\"LastPlayed\""));
    }

    /// The common case on a fresh account: nothing below `Steam` exists yet.
    #[test]
    fn creates_the_missing_chain() {
        let src = "\"UserLocalConfigStore\"\n{\n\t\"friends\"\n\t{\n\t\t\"SignIntoFriends\"\t\t\"1\"\n\t}\n}\n";
        let out = set_nested_key(src, &APPS_PATH, "LaunchOptions", "-novid");
        for name in APPS_PATH {
            assert!(out.contains(&format!("\"{name}\"")), "missing section {name}");
        }
        assert!(out.contains("\"LaunchOptions\"\t\t\"-novid\""));
        assert!(out.contains("\"SignIntoFriends\"\t\t\"1\""));
        // Braces must still balance, or Steam discards the whole file.
        assert_eq!(
            out.matches('{').count(),
            out.matches('}').count(),
            "unbalanced braces:\n{out}"
        );
    }

    /// Re-running must be a no-op, not a second copy of the chain.
    #[test]
    fn is_idempotent() {
        let src = "\"UserLocalConfigStore\"\n{\n}\n";
        let once = set_nested_key(src, &APPS_PATH, "LaunchOptions", "-novid");
        let twice = set_nested_key(&once, &APPS_PATH, "LaunchOptions", "-novid");
        assert_eq!(once, twice);
        assert_eq!(twice.matches("\"730\"").count(), 1);
    }

    /// A same-named key in a different branch must not be mistaken for ours.
    #[test]
    fn ignores_the_same_key_elsewhere() {
        let src = "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t\t\"LaunchOptions\"\t\t\"-decoy\"\n\t}\n}\n";
        let out = set_nested_key(src, &APPS_PATH, "LaunchOptions", "-novid");
        assert!(out.contains("\"-decoy\""), "the unrelated key was overwritten");
        assert!(out.contains("\"-novid\""));
    }

    #[test]
    fn writes_a_shallow_path() {
        let src = "\"UserLocalConfigStore\"\n{\n}\n";
        let out = set_nested_key(src, &["streaming_v2"], "EnableStreaming", "0");
        assert!(out.contains("\"streaming_v2\""));
        assert!(out.contains("\"EnableStreaming\"\t\t\"0\""));
        assert_eq!(out.matches('{').count(), out.matches('}').count());
    }
}
