use std::time::Duration;

use bashkit::ExecutionLimits;

pub const MAX_RPC_FRAME_BYTES: usize = 8 * 1024 * 1024;
pub const COMMAND_TIMEOUT: Duration = Duration::from_secs(15);
pub const MAX_TERMINAL_OUTPUT_BYTES: usize = 1024 * 1024;
pub const MAX_COMMANDS: usize = 2_000;
pub const MAX_LOOP_ITERATIONS: usize = 5_000;
pub const MAX_FUNCTION_DEPTH: usize = 16;
pub const MAX_STRING_BYTES: usize = 2 * 1024 * 1024;
pub const MAX_GLOB_EXPANSION_RESULTS: usize = 10_000;
pub const MAX_BRACE_EXPANSION_RESULTS: usize = 2_000;

pub fn execution_limits() -> ExecutionLimits {
    ExecutionLimits::new()
        .timeout(COMMAND_TIMEOUT)
        .max_commands(MAX_COMMANDS)
        .max_loop_iterations(MAX_LOOP_ITERATIONS)
        .max_total_loop_iterations(MAX_LOOP_ITERATIONS)
        .max_function_depth(MAX_FUNCTION_DEPTH)
        .max_subshell_depth(MAX_FUNCTION_DEPTH)
        .max_subst_depth(MAX_FUNCTION_DEPTH)
        .max_stdout_bytes(MAX_TERMINAL_OUTPUT_BYTES)
        .max_stderr_bytes(MAX_TERMINAL_OUTPUT_BYTES)
        .max_input_bytes(MAX_STRING_BYTES)
        .max_word_split_bytes(MAX_STRING_BYTES)
}

pub fn truncate_complete_lines(text: &str, max_bytes: usize) -> (String, bool) {
    if text.len() < max_bytes {
        return (text.to_string(), false);
    }
    let mut boundary = max_bytes;
    while boundary > 0 && !text.is_char_boundary(boundary) {
        boundary -= 1;
    }
    let slice = &text[..boundary];
    if let Some(idx) = slice.rfind('\n') {
        (slice[..=idx].to_string(), true)
    } else {
        (String::new(), true)
    }
}

/// Append one streaming chunk pair in the order Bashkit delivers it.
/// Empty sides are skipped so the merged stream preserves occurrence order.
pub fn append_stream_chunk(merged: &mut String, stdout_chunk: &str, stderr_chunk: &str) {
    if !stdout_chunk.is_empty() {
        merged.push_str(stdout_chunk);
    }
    if !stderr_chunk.is_empty() {
        merged.push_str(stderr_chunk);
    }
}

pub fn append_exit_trailer(mut text: String, exit_code: i32) -> String {
    if exit_code == 0 {
        return text;
    }
    if !text.is_empty() && !text.ends_with('\n') {
        text.push('\n');
    }
    text.push_str(&format!("[exit {exit_code}]\n"));
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truncate_keeps_complete_lines_only() {
        let (text, truncated) = truncate_complete_lines("one\ntwo\nthree\n", 8);
        assert!(truncated);
        assert_eq!(text, "one\ntwo\n");
    }

    #[test]
    fn truncate_empty_when_no_newline_in_window() {
        let (text, truncated) = truncate_complete_lines("abcdefghij", 4);
        assert!(truncated);
        assert!(text.is_empty());
    }

    #[test]
    fn truncate_does_not_split_utf8_codepoint() {
        let (text, truncated) = truncate_complete_lines("甲乙\n丙丁", 8);
        assert!(truncated);
        assert_eq!(text, "甲乙\n");
    }

    #[test]
    fn stream_chunks_preserve_occurrence_order() {
        let mut merged = String::new();
        append_stream_chunk(&mut merged, "out1\n", "");
        append_stream_chunk(&mut merged, "", "err1\n");
        append_stream_chunk(&mut merged, "out2\n", "err2\n");
        assert_eq!(merged, "out1\nerr1\nout2\nerr2\n");
    }

    #[test]
    fn exit_trailer_only_for_nonzero() {
        assert_eq!(append_exit_trailer("ok\n".into(), 0), "ok\n");
        assert_eq!(append_exit_trailer("fail".into(), 1), "fail\n[exit 1]\n");
    }
}
