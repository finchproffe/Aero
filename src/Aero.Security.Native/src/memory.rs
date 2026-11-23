//! Secrets live outside the .NET heap: the GC never moves or copies them, they are locked out of
//! the pagefile, excluded from WER crash dumps, fenced by no-access guard pages, wiped on free,
//! and long-lived keys can be kept encrypted in place (CryptProtectMemory) between uses.

use std::ptr::{self, NonNull};
use std::sync::OnceLock;

use windows_sys::Win32::Security::Cryptography::{
    BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom, CRYPTPROTECTMEMORY_BLOCK_SIZE,
    CRYPTPROTECTMEMORY_SAME_PROCESS, CryptProtectMemory, CryptUnprotectMemory,
};
use windows_sys::Win32::System::ErrorReporting::{
    WerRegisterExcludedMemoryBlock, WerUnregisterExcludedMemoryBlock,
};
use windows_sys::Win32::System::Memory::{
    MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_NOACCESS, PAGE_READWRITE, VirtualAlloc,
    VirtualFree, VirtualLock, VirtualProtect, VirtualUnlock,
};
use windows_sys::Win32::System::SystemInformation::{GetSystemInfo, SYSTEM_INFO};
use zeroize::Zeroize;

fn page_size() -> usize {
    static PAGE: OnceLock<usize> = OnceLock::new();
    *PAGE.get_or_init(|| {
        let mut info: SYSTEM_INFO = unsafe { std::mem::zeroed() };
        unsafe { GetSystemInfo(&mut info) };
        info.dwPageSize as usize
    })
}

/// Fills `buffer` from the OS CSPRNG.
pub fn fill_random(buffer: &mut [u8]) -> bool {
    buffer.chunks_mut(u32::MAX as usize).all(|chunk| unsafe {
        BCryptGenRandom(
            ptr::null_mut(),
            chunk.as_mut_ptr(),
            chunk.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        ) >= 0
    })
}

pub struct SecretBox {
    base: NonNull<u8>,
    data: NonNull<u8>,
    len: usize,
    /// `len` rounded up to the CryptProtectMemory block, never zero.
    cap: usize,
    locked: bool,
    wer_excluded: bool,
    concealed: bool,
}

// The box is plain memory; .NET serialises mutation (conceal happens before the handle is shared).
unsafe impl Send for SecretBox {}
unsafe impl Sync for SecretBox {}

impl SecretBox {
    /// Zero-filled secret of `len` bytes, or None when Windows refuses the allocation.
    pub fn new(len: usize) -> Option<Self> {
        let cap = len.max(1).checked_next_multiple_of(CRYPTPROTECTMEMORY_BLOCK_SIZE as usize)?;
        let page = page_size();
        let body = cap.checked_next_multiple_of(page)?;
        let total = body.checked_add(2 * page)?;
        unsafe {
            let base = NonNull::new(
                VirtualAlloc(ptr::null(), total, MEM_RESERVE | MEM_COMMIT, PAGE_NOACCESS).cast::<u8>(),
            )?;
            let mut previous = 0;
            if VirtualProtect(base.as_ptr().add(page).cast(), body, PAGE_READWRITE, &mut previous) == 0 {
                VirtualFree(base.as_ptr().cast(), 0, MEM_RELEASE);
                return None;
            }

            // Right-aligned on the trailing guard page, so an overrun faults instead of leaking.
            let data = NonNull::new_unchecked(base.as_ptr().add(page + body - cap));
            let locked = VirtualLock(data.as_ptr().cast(), cap) != 0;
            let wer_excluded = u32::try_from(cap)
                .is_ok_and(|size| WerRegisterExcludedMemoryBlock(data.as_ptr().cast(), size) >= 0);
            // VirtualAlloc hands out zeroed pages.
            Some(Self { base, data, len, cap, locked, wer_excluded, concealed: false })
        }
    }

    pub fn random(len: usize) -> Option<Self> {
        let mut secret = Self::new(len)?;
        fill_random(secret.as_mut_slice()).then_some(secret)
    }

    pub fn from_slice(source: &[u8]) -> Option<Self> {
        let mut secret = Self::new(source.len())?;
        secret.as_mut_slice().copy_from_slice(source);
        Some(secret)
    }

    pub fn len(&self) -> usize {
        self.len
    }

    pub fn is_locked(&self) -> bool {
        self.locked
    }

    pub fn is_concealed(&self) -> bool {
        self.concealed
    }

    /// Raw bytes; ciphertext while concealed.
    pub fn as_slice(&self) -> &[u8] {
        unsafe { std::slice::from_raw_parts(self.data.as_ptr(), self.len) }
    }

    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        unsafe { std::slice::from_raw_parts_mut(self.data.as_ptr(), self.len) }
    }

    pub fn data_ptr(&self) -> *mut u8 {
        self.data.as_ptr()
    }

    /// Encrypts the secret in place with a key only this process can use. A memory dump or a
    /// stealer reading process memory then sees ciphertext. Irreversible for this box.
    pub fn conceal(&mut self) -> bool {
        if !self.concealed {
            self.concealed = unsafe {
                CryptProtectMemory(self.data.as_ptr().cast(), self.cap as u32, CRYPTPROTECTMEMORY_SAME_PROCESS) != 0
            };
        }
        self.concealed
    }

    /// Runs `use_plain` on the plaintext. A concealed secret is decrypted into a temporary
    /// guarded box that is wiped as soon as `use_plain` returns; the original is never touched.
    pub fn with_plain<R>(&self, use_plain: impl FnOnce(&[u8]) -> R) -> Option<R> {
        if !self.concealed {
            return Some(use_plain(self.as_slice()));
        }

        let plain = SecretBox::new(self.cap)?;
        unsafe {
            ptr::copy_nonoverlapping(self.data.as_ptr(), plain.data.as_ptr(), self.cap);
            if CryptUnprotectMemory(plain.data.as_ptr().cast(), self.cap as u32, CRYPTPROTECTMEMORY_SAME_PROCESS) == 0 {
                return None;
            }
        }
        Some(use_plain(&plain.as_slice()[..self.len]))
    }
}

impl Drop for SecretBox {
    fn drop(&mut self) {
        unsafe {
            std::slice::from_raw_parts_mut(self.data.as_ptr(), self.cap).zeroize();
            if self.wer_excluded {
                WerUnregisterExcludedMemoryBlock(self.data.as_ptr().cast());
            }
            if self.locked {
                VirtualUnlock(self.data.as_ptr().cast(), self.cap);
            }
            VirtualFree(self.base.as_ptr().cast(), 0, MEM_RELEASE);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows_sys::Win32::System::Memory::{MEMORY_BASIC_INFORMATION, VirtualQuery};

    fn protection_at(address: *const u8) -> u32 {
        let mut info: MEMORY_BASIC_INFORMATION = unsafe { std::mem::zeroed() };
        let size = std::mem::size_of::<MEMORY_BASIC_INFORMATION>();
        assert_eq!(unsafe { VirtualQuery(address.cast(), &mut info, size) }, size);
        info.Protect
    }

    #[test]
    fn new_is_zeroed_and_writable() {
        let mut secret = SecretBox::new(37).unwrap();
        assert!(secret.as_slice().iter().all(|&b| b == 0));
        secret.as_mut_slice().fill(0xAB);
        assert_eq!(secret.len(), 37);
        assert!(secret.as_slice().iter().all(|&b| b == 0xAB));
    }

    #[test]
    fn empty_secret_is_valid() {
        let secret = SecretBox::new(0).unwrap();
        assert!(secret.as_slice().is_empty());
    }

    #[test]
    fn guard_pages_fence_the_secret() {
        let secret = SecretBox::new(32).unwrap();
        let end = unsafe { secret.data_ptr().add(secret.cap) };
        assert_eq!(protection_at(end), PAGE_NOACCESS, "no guard page right after the secret");
        assert_eq!(protection_at(secret.base.as_ptr()), PAGE_NOACCESS, "no leading guard page");
        assert_eq!(protection_at(secret.data_ptr()), PAGE_READWRITE);
    }

    #[test]
    fn random_is_not_constant() {
        let a = SecretBox::random(32).unwrap();
        let b = SecretBox::random(32).unwrap();
        assert_ne!(a.as_slice(), b.as_slice());
        assert!(a.as_slice().iter().any(|&b| b != 0));
    }

    #[test]
    fn conceal_hides_plaintext_and_with_plain_recovers_it() {
        let original: Vec<u8> = (0u8..32).collect();
        let mut secret = SecretBox::from_slice(&original).unwrap();
        assert!(secret.conceal());
        assert!(secret.is_concealed());
        assert_ne!(secret.as_slice(), &original[..], "concealed memory still holds plaintext");
        assert_eq!(secret.with_plain(|plain| plain.to_vec()).unwrap(), original);
        // Decrypting for use must not unconceal the stored copy.
        assert_ne!(secret.as_slice(), &original[..]);
    }
}
