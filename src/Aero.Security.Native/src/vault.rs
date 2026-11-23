//! The Aero sealed-file container, byte-compatible with the earlier .NET AesGcm version:
//! version (1) | nonce (12) | tag (16) | AES-256-GCM ciphertext, with the context as AAD.
//! The key is only ever read inside a guarded box; plaintext out of `open` lands in one too.

use aes_gcm::aead::AeadInPlace;
use aes_gcm::{Aes256Gcm, KeyInit, Nonce, Tag};

use crate::memory::{SecretBox, fill_random};

pub const VERSION: u8 = 1;
const NONCE: usize = 12;
const TAG: usize = 16;
pub const HEADER: usize = 1 + NONCE + TAG;
const KEY: usize = 32;

#[derive(Debug, PartialEq, Eq)]
pub enum VaultError {
    InvalidArgument,
    /// Wrong key, wrong context, tampered data or unknown format: always fail closed.
    Rejected,
    Os,
}

fn cipher(key: &SecretBox) -> Result<Aes256Gcm, VaultError> {
    if key.len() != KEY {
        return Err(VaultError::InvalidArgument);
    }
    key.with_plain(|plain| Aes256Gcm::new_from_slice(plain).map_err(|_| VaultError::InvalidArgument))
        .ok_or(VaultError::Os)?
}

/// Seals `plain` into `out`, which must be exactly `HEADER + plain.len()` bytes.
pub fn seal(key: &SecretBox, plain: &[u8], context: &[u8], out: &mut [u8]) -> Result<(), VaultError> {
    if out.len() != HEADER + plain.len() {
        return Err(VaultError::InvalidArgument);
    }

    let cipher = cipher(key)?;
    let (head, body) = out.split_at_mut(HEADER);
    head[0] = VERSION;
    if !fill_random(&mut head[1..1 + NONCE]) {
        return Err(VaultError::Os);
    }
    body.copy_from_slice(plain);
    let tag = cipher
        .encrypt_in_place_detached(Nonce::from_slice(&head[1..1 + NONCE]), context, body)
        .map_err(|_| VaultError::InvalidArgument)?;
    head[1 + NONCE..].copy_from_slice(&tag);
    Ok(())
}

pub fn open(key: &SecretBox, sealed: &[u8], context: &[u8]) -> Result<SecretBox, VaultError> {
    if sealed.len() < HEADER || sealed[0] != VERSION {
        return Err(VaultError::Rejected);
    }

    let cipher = cipher(key)?;
    let mut plain = SecretBox::from_slice(&sealed[HEADER..]).ok_or(VaultError::Os)?;
    // On failure `plain` drops here and is wiped; nothing unauthenticated escapes.
    cipher
        .decrypt_in_place_detached(
            Nonce::from_slice(&sealed[1..1 + NONCE]),
            context,
            plain.as_mut_slice(),
            Tag::from_slice(&sealed[1 + NONCE..HEADER]),
        )
        .map_err(|_| VaultError::Rejected)?;
    Ok(plain)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sealed(key: &SecretBox, plain: &[u8], context: &[u8]) -> Vec<u8> {
        let mut out = vec![0; HEADER + plain.len()];
        seal(key, plain, context, &mut out).unwrap();
        out
    }

    #[test]
    fn round_trips_and_hides_plaintext() {
        let key = SecretBox::random(32).unwrap();
        let data = sealed(&key, b"SID=youtube-session", b"profile-a");
        assert!(!data.windows(7).any(|w| w == b"youtube"));
        assert_eq!(open(&key, &data, b"profile-a").unwrap().as_slice(), b"SID=youtube-session");
    }

    #[test]
    fn concealed_key_works() {
        let mut key = SecretBox::random(32).unwrap();
        let data = sealed(&key, b"cookie", b"ctx");
        assert!(key.conceal());
        assert_eq!(open(&key, &data, b"ctx").unwrap().as_slice(), b"cookie");
        let again = sealed(&key, b"cookie2", b"ctx");
        assert_eq!(open(&key, &again, b"ctx").unwrap().as_slice(), b"cookie2");
    }

    #[test]
    fn rejects_wrong_context_key_tamper_and_format() {
        let key = SecretBox::random(32).unwrap();
        let other = SecretBox::random(32).unwrap();
        let mut data = sealed(&key, b"payload", b"profile-a");
        assert_eq!(open(&key, &data, b"profile-b").err(), Some(VaultError::Rejected));
        assert_eq!(open(&other, &data, b"profile-a").err(), Some(VaultError::Rejected));
        assert_eq!(open(&key, &data[..HEADER - 1], b"profile-a").err(), Some(VaultError::Rejected));
        data[0] = 2;
        assert_eq!(open(&key, &data, b"profile-a").err(), Some(VaultError::Rejected));
        data[0] = VERSION;
        *data.last_mut().unwrap() ^= 1;
        assert_eq!(open(&key, &data, b"profile-a").err(), Some(VaultError::Rejected));
    }

    #[test]
    fn empty_payload_and_bad_sizes() {
        let key = SecretBox::random(32).unwrap();
        assert!(open(&key, &sealed(&key, b"", b"c"), b"c").unwrap().as_slice().is_empty());
        let short_key = SecretBox::random(16).unwrap();
        assert_eq!(seal(&short_key, b"x", b"c", &mut [0; HEADER + 1]).err(), Some(VaultError::InvalidArgument));
        assert_eq!(seal(&key, b"x", b"c", &mut [0; HEADER]).err(), Some(VaultError::InvalidArgument));
    }

    #[test]
    fn nonces_differ() {
        let key = SecretBox::random(32).unwrap();
        assert_ne!(sealed(&key, b"same", b"c")[1..HEADER], sealed(&key, b"same", b"c")[1..HEADER]);
    }
}
