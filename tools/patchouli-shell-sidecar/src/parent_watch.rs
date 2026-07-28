use std::time::Duration;

/// Exit the process if the parent Patchouli host disappears.
///
/// - Linux: also installs PR_SET_PDEATHSIG for immediate termination.
/// - All platforms: poll PATCHOULI_PARENT_PID (set by the host).
/// - stdin EOF remains the primary graceful path for clean host shutdown.
pub fn spawn() {
    #[cfg(target_os = "linux")]
    install_linux_pdeathsig();

    let Some(parent_pid) = std::env::var("PATCHOULI_PARENT_PID")
        .ok()
        .and_then(|value| value.parse::<u32>().ok())
        .filter(|pid| *pid > 0)
    else {
        return;
    };

    tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_millis(500));
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            interval.tick().await;
            if !parent_alive(parent_pid) {
                // Host is gone; do not leave an orphaned sandbox.
                std::process::exit(0);
            }
        }
    });
}

#[cfg(target_os = "linux")]
fn install_linux_pdeathsig() {
    // SAFETY: prctl PR_SET_PDEATHSIG is process-local and takes a signal number.
    unsafe {
        libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGTERM, 0, 0, 0);
        // Close the race where the parent died before prctl completed.
        if libc::getppid() == 1 {
            std::process::exit(0);
        }
    }
}

#[cfg(unix)]
fn parent_alive(pid: u32) -> bool {
    // SAFETY: kill(pid, 0) only checks existence/permissions; no signal is delivered.
    unsafe { libc::kill(pid as libc::pid_t, 0) == 0 }
}

#[cfg(windows)]
fn parent_alive(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, WAIT_TIMEOUT};
    use windows_sys::Win32::System::Threading::{
        OpenProcess, WaitForSingleObject, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
    };

    // SAFETY: OpenProcess/WaitForSingleObject/CloseHandle on a valid PID query handle.
    unsafe {
        let handle = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            pid,
        );
        if handle.is_null() || handle == (-1isize as *mut _) {
            return false;
        }
        let status = WaitForSingleObject(handle, 0);
        let _ = CloseHandle(handle);
        status == WAIT_TIMEOUT
    }
}

#[cfg(not(any(unix, windows)))]
fn parent_alive(_pid: u32) -> bool {
    true
}
