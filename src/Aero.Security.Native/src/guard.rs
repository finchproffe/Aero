//! Info-stealers running as the same user open the browser with PROCESS_VM_READ to scrape
//! cookies, tokens and typed passwords from memory, or with VM_WRITE/CREATE_THREAD to inject.
//! The guard puts a deny ACE for the user in front of the process DACL and an OWNER RIGHTS ACE
//! that removes the owner's implicit WRITE_DAC, so a non-admin stealer cannot rewrite the DACL
//! back. Existing grants stay: Chromium sandbox processes die without their AppContainer/logon
//! SID entries. Admins with SeDebugPrivilege still bypass this.

use std::ffi::c_void;
use std::ptr;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, LocalFree};
use windows_sys::Win32::Security::Authorization::{
    ConvertSecurityDescriptorToStringSecurityDescriptorW, ConvertSidToStringSidW,
    ConvertStringSecurityDescriptorToSecurityDescriptorW, GetSecurityInfo, SDDL_REVISION_1,
    SE_KERNEL_OBJECT,
};
use windows_sys::Win32::Security::{
    DACL_SECURITY_INFORMATION, GetTokenInformation, SetKernelObjectSecurity, TOKEN_QUERY,
    TOKEN_USER, TokenUser,
};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, GetCurrentProcessId, OpenProcess, OpenProcessToken,
    PROCESS_QUERY_LIMITED_INFORMATION, ProcessExtensionPointDisablePolicy, ProcessImageLoadPolicy,
    SetProcessMitigationPolicy,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{SPI_GETSCREENREADER, SystemParametersInfoW};

/// CREATE_THREAD | VM_OPERATION | VM_READ | VM_WRITE | DUP_HANDLE (equals full access) |
/// CREATE_PROCESS (parent spoofing) | SET_QUOTA | SET_INFORMATION | SUSPEND_RESUME |
/// SET_LIMITED_INFORMATION | WRITE_DAC | WRITE_OWNER. Terminate, query and wait stay allowed.
pub const DENIED_USER_ACCESS: u32 = 0x000C_2BFA;
const PROCESS_DUP_HANDLE: u32 = 0x0000_0040;
const READ_CONTROL: u32 = 0x0002_0000;
const WRITE_DAC: u32 = 0x0004_0000;
/// NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images: blocks DLL planting from
/// network shares and low-integrity (download / temp) locations.
const IMAGE_LOAD_RESTRICTIONS: u32 = 0x7;
const EXTENSION_POINTS_DISABLED: u32 = 0x1;

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

struct Local(*mut c_void);
impl Drop for Local {
    fn drop(&mut self) {
        unsafe { LocalFree(self.0) };
    }
}

unsafe fn wide_to_string(text: *const u16) -> Option<String> {
    if text.is_null() {
        return None;
    }
    let len = (0..).take_while(|&i| unsafe { *text.add(i) } != 0).count();
    String::from_utf16(unsafe { std::slice::from_raw_parts(text, len) }).ok()
}

fn current_user_sid() -> Option<String> {
    unsafe {
        let mut token = ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let token = Handle(token);
        let mut size = 0;
        GetTokenInformation(token.0, TokenUser, ptr::null_mut(), 0, &mut size);
        let mut buffer = vec![0u64; (size as usize).div_ceil(8)]; // u64 keeps TOKEN_USER aligned
        if size == 0 || GetTokenInformation(token.0, TokenUser, buffer.as_mut_ptr().cast(), size, &mut size) == 0 {
            return None;
        }
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        let mut text = ptr::null_mut();
        if ConvertSidToStringSidW(user.User.Sid, &mut text) == 0 {
            return None;
        }
        let text = Local(text.cast());
        wide_to_string(text.0.cast())
    }
}

/// Puts the deny and OWNER RIGHTS ACEs in front of the existing ACEs of a "D:..." SDDL.
pub fn add_guard_aces(dacl: &str, user_sid: &str, denied: u32) -> Option<String> {
    // A NULL DACL ("D:NO_ACCESS_CONTROL") cannot be extended safely.
    if !dacl.starts_with("D:") || dacl.contains("NO_ACCESS_CONTROL") {
        return None;
    }
    let aces = format!("(D;;0x{denied:x};;;{user_sid})(A;;RC;;;OW)");
    let first_ace = dacl.find('(').unwrap_or(dacl.len());
    Some(format!("{}{aces}{}", &dacl[..first_ace], &dacl[first_ace..]))
}

/// Guards a same-user process. False when the process is gone or already guarded: the guard
/// itself removes our WRITE_DAC. WebView2 processes cannot be guarded: Chromium opens its own
/// processes for VM access and they crash when that is denied.
pub fn restrict(process_id: u32, denied: u32) -> bool {
    if process_id == 0 {
        return false;
    }
    let Some(sid) = current_user_sid() else {
        return false;
    };

    unsafe {
        let process = OpenProcess(READ_CONTROL | WRITE_DAC | PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id);
        if process.is_null() {
            return false;
        }
        let process = Handle(process);

        let mut current = ptr::null_mut();
        if GetSecurityInfo(
            process.0,
            SE_KERNEL_OBJECT,
            DACL_SECURITY_INFORMATION,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            &mut current,
        ) != 0
        {
            return false;
        }
        let current = Local(current);

        let mut text = ptr::null_mut();
        if ConvertSecurityDescriptorToStringSecurityDescriptorW(
            current.0,
            SDDL_REVISION_1,
            DACL_SECURITY_INFORMATION,
            &mut text,
            ptr::null_mut(),
        ) == 0
        {
            return false;
        }
        let text = Local(text.cast());
        let Some(guarded) = wide_to_string(text.0.cast()).and_then(|dacl| add_guard_aces(&dacl, &sid, denied)) else {
            return false;
        };

        let wide: Vec<u16> = guarded.encode_utf16().chain(Some(0)).collect();
        let mut descriptor = ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            wide.as_ptr(),
            SDDL_REVISION_1,
            &mut descriptor,
            ptr::null_mut(),
        ) == 0
        {
            return false;
        }
        let descriptor = Local(descriptor);
        SetKernelObjectSecurity(process.0, DACL_SECURITY_INFORMATION, descriptor.0) != 0
    }
}

/// Ids of the running processes with this image name whose parent is `parent_id`.
fn child_processes(parent_id: u32, image_name: &str) -> Vec<u32> {
    let mut children = Vec::new();
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot.is_null() || snapshot as isize == -1 {
            return children;
        }
        let snapshot = Handle(snapshot);
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = size_of::<PROCESSENTRY32W>() as u32;
        let mut more = Process32FirstW(snapshot.0, &mut entry) != 0;
        while more {
            let len = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
            if entry.th32ParentProcessID == parent_id
                && String::from_utf16_lossy(&entry.szExeFile[..len]).eq_ignore_ascii_case(image_name)
            {
                children.push(entry.th32ProcessID);
            }
            more = Process32NextW(snapshot.0, &mut entry) != 0;
        }
    }
    children
}

/// Guards a WebView2 process and its msedgewebview2.exe children that WebView2 does not report
/// (the crashpad handler holds read handles to every other WebView2 process and writes dumps).
/// Only that image name is touched, so an unrelated process that reused a PID never is.
/// Returns how many processes this call guarded; already guarded ones are skipped.
pub fn restrict_webview_tree(process_id: u32, denied: u32) -> u32 {
    std::iter::once(process_id)
        .chain(child_processes(process_id, "msedgewebview2.exe"))
        .filter(|&id| restrict(id, denied))
        .count() as u32
}

fn set_mitigation(policy: i32, flags: u32) -> bool {
    unsafe { SetProcessMitigationPolicy(policy, (&raw const flags).cast(), size_of::<u32>()) != 0 }
}

fn screen_reader_running() -> bool {
    let mut running: i32 = 0;
    unsafe { SystemParametersInfoW(SPI_GETSCREENREADER, 0, (&raw mut running).cast(), 0) != 0 && running != 0 }
}

/// Loader mitigations plus the access guard for the current process.
pub fn harden_current_process() -> bool {
    let mut hardened = set_mitigation(ProcessImageLoadPolicy, IMAGE_LOAD_RESTRICTIONS);

    // Blocks AppInit_DLLs, Winsock LSPs and SetWindowsHookEx DLL injection. Some screen readers
    // inject the same way, so the mitigation is skipped while one is running.
    if !screen_reader_running() {
        hardened &= set_mitigation(ProcessExtensionPointDisablePolicy, EXTENSION_POINTS_DISABLED);
    }

    // The WebView2 browser process duplicates shared-buffer handles (password capture and
    // autofill) into Aero, so Aero cannot deny DUP_HANDLE. That right lets a stealer that knows
    // the pseudo-handle trick regain full access; plain OpenProcess dumpers stay blocked.
    restrict(unsafe { GetCurrentProcessId() }, DENIED_USER_ACCESS & !PROCESS_DUP_HANDLE) && hardened
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::{Command, Stdio};
    use windows_sys::Win32::System::Threading::PROCESS_VM_READ;
    const SYNCHRONIZE: u32 = 0x0010_0000;

    #[test]
    fn deny_ace_precedes_every_grant() {
        assert_eq!(
            add_guard_aces("D:(A;;GA;;;SY)", "S-1-5-21-1", DENIED_USER_ACCESS).unwrap(),
            "D:(D;;0xc2bfa;;;S-1-5-21-1)(A;;RC;;;OW)(A;;GA;;;SY)"
        );
        assert_eq!(
            add_guard_aces("D:P(A;;GA;;;SY)", "S-1-5-21-1", 0x40).unwrap(),
            "D:P(D;;0x40;;;S-1-5-21-1)(A;;RC;;;OW)(A;;GA;;;SY)"
        );
        assert_eq!(add_guard_aces("D:", "S-1", 1).unwrap(), "D:(D;;0x1;;;S-1)(A;;RC;;;OW)");
    }

    #[test]
    fn null_or_foreign_dacl_is_not_guarded_blindly() {
        assert!(add_guard_aces("D:NO_ACCESS_CONTROL", "S-1-5-21-1", DENIED_USER_ACCESS).is_none());
        assert!(add_guard_aces("O:BA", "S-1-5-21-1", DENIED_USER_ACCESS).is_none());
        assert!(add_guard_aces("", "S-1-5-21-1", DENIED_USER_ACCESS).is_none());
    }

    #[test]
    fn finds_children_by_parent_and_image() {
        let mut child = Command::new("cmd.exe").args(["/c", "ping -n 5 127.0.0.1 >nul"]).spawn().unwrap();
        std::thread::sleep(std::time::Duration::from_millis(300));
        let me = unsafe { GetCurrentProcessId() };
        assert!(child_processes(me, "cmd.exe").contains(&child.id()));
        assert!(child_processes(me, "msedgewebview2.exe").is_empty(), "matched a process by parent alone");
        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn user_sid_is_resolved() {
        assert!(current_user_sid().unwrap().starts_with("S-1-5-"));
    }

    #[test]
    fn guarded_process_refuses_memory_reads_but_can_be_waited_on() {
        let mut child = Command::new("ping.exe")
            .args(["-n", "30", "127.0.0.1"])
            .stdout(Stdio::null())
            .spawn()
            .unwrap();
        let pid = child.id();
        let result = std::panic::catch_unwind(|| {
            assert!(restrict(pid, DENIED_USER_ACCESS), "the DACL could not be restricted");
            unsafe {
                assert!(OpenProcess(PROCESS_VM_READ, 0, pid).is_null(), "memory is still readable");
                let wait = OpenProcess(SYNCHRONIZE, 0, pid);
                assert!(!wait.is_null(), "the guarded process can no longer be waited on");
                CloseHandle(wait);
            }
            assert!(!restrict(pid, DENIED_USER_ACCESS), "the owner can still rewrite the DACL");
        });
        let _ = child.kill();
        let _ = child.wait();
        result.unwrap();
    }
}
