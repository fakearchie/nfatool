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
