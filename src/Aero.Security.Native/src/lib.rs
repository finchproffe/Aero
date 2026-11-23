//! Aero anti-stealer core, called from .NET (Aero.Security) through a small C ABI.
//! Every handle is a `SecretBox` owned by a .NET SafeHandle; status codes are below.
//! Safety contract for every export: handles come from `aero_secret_new`/`aero_vault_open` and
//! are still alive, and each (pointer, length) pair is valid for that many bytes.
#![allow(clippy::missing_safety_doc)]

mod guard;
mod memory;
mod vault;

use memory::SecretBox;
use vault::VaultError;

pub const OK: i32 = 0;
pub const INVALID_ARGUMENT: i32 = 1;
pub const REJECTED: i32 = 2;
pub const OS_FAILURE: i32 = 3;

impl From<VaultError> for i32 {
    fn from(error: VaultError) -> i32 {
        match error {
            VaultError::InvalidArgument => INVALID_ARGUMENT,
            VaultError::Rejected => REJECTED,
            VaultError::Os => OS_FAILURE,
        }
    }
}

/// .NET passes (null, 0) for empty spans; `from_raw_parts` needs a non-null pointer.
unsafe fn bytes<'a>(pointer: *const u8, len: usize) -> &'a [u8] {
    if len == 0 { &[] } else { unsafe { std::slice::from_raw_parts(pointer, len) } }
}

fn into_handle(secret: SecretBox, out: *mut *mut SecretBox) -> i32 {
    unsafe { *out = Box::into_raw(Box::new(secret)) };
    OK
}

/// New protected secret of `len` bytes, zeroed or (random != 0) from the OS CSPRNG.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_new(len: usize, random: i32, out: *mut *mut SecretBox) -> i32 {
    if out.is_null() {
        return INVALID_ARGUMENT;
    }
    unsafe { *out = std::ptr::null_mut() };
    match if random != 0 { SecretBox::random(len) } else { SecretBox::new(len) } {
        Some(secret) => into_handle(secret, out),
        None => OS_FAILURE,
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_free(secret: *mut SecretBox) {
    if !secret.is_null() {
        drop(unsafe { Box::from_raw(secret) });
    }
}

/// Writable plaintext pointer, or null while concealed (the plaintext is not there).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_data(secret: *const SecretBox) -> *mut u8 {
    let secret = unsafe { &*secret };
    if secret.is_concealed() { std::ptr::null_mut() } else { secret.data_ptr() }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_len(secret: *const SecretBox) -> usize {
    unsafe { &*secret }.len()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_is_locked(secret: *const SecretBox) -> i32 {
    unsafe { &*secret }.is_locked() as i32
}

/// Encrypts the secret in place for the rest of its life; call before sharing the handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_conceal(secret: *mut SecretBox) -> i32 {
    if unsafe { &mut *secret }.conceal() { OK } else { OS_FAILURE }
}

/// Copies the plaintext (decrypting a concealed secret) into `destination` of exactly `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_secret_reveal(secret: *const SecretBox, destination: *mut u8, len: usize) -> i32 {
    let secret = unsafe { &*secret };
    if len != secret.len() || (len != 0 && destination.is_null()) {
        return INVALID_ARGUMENT;
    }
    match secret.with_plain(|plain| unsafe { std::ptr::copy_nonoverlapping(plain.as_ptr(), destination, len) }) {
        Some(()) => OK,
        None => OS_FAILURE,
    }
}

/// Seals into `out`, which must be exactly 29 + `plain_len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_vault_seal(
    key: *const SecretBox,
    plain: *const u8,
    plain_len: usize,
    context: *const u8,
    context_len: usize,
    out: *mut u8,
    out_len: usize,
) -> i32 {
    if out.is_null() {
        return INVALID_ARGUMENT;
    }
    unsafe {
        let out = std::slice::from_raw_parts_mut(out, out_len);
        match vault::seal(&*key, bytes(plain, plain_len), bytes(context, context_len), out) {
            Ok(()) => OK,
            Err(error) => error.into(),
        }
    }
}

/// Opens a sealed blob; the plaintext is returned as a new protected secret.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn aero_vault_open(
    key: *const SecretBox,
    sealed: *const u8,
    sealed_len: usize,
    context: *const u8,
    context_len: usize,
    out: *mut *mut SecretBox,
) -> i32 {
    if out.is_null() {
        return INVALID_ARGUMENT;
    }
    unsafe {
        *out = std::ptr::null_mut();
        match vault::open(&*key, bytes(sealed, sealed_len), bytes(context, context_len)) {
            Ok(plain) => into_handle(plain, out),
            Err(error) => error.into(),
        }
    }
}

/// 1 when every loader mitigation and the access guard applied to this process.
#[unsafe(no_mangle)]
pub extern "C" fn aero_guard_harden_current_process() -> i32 {
    guard::harden_current_process() as i32
}

/// 1 when the deny ACE was written to the process DACL.
#[unsafe(no_mangle)]
pub extern "C" fn aero_guard_restrict(process_id: u32, denied_access: u32) -> i32 {
    guard::restrict(process_id, denied_access) as i32
}

/// Guards a WebView2 process plus its unreported msedgewebview2.exe children; returns the count.
#[unsafe(no_mangle)]
pub extern "C" fn aero_guard_restrict_webview_tree(process_id: u32, denied_access: u32) -> u32 {
    guard::restrict_webview_tree(process_id, denied_access)
}
