# Injector delivery protocol (v1)

How an injector client such as Launchpad installs a mod that its creator marked **Install through the injector**.

These mods are never served by `/api/v1/downloads`. Their files are encrypted on disk, and the server only releases a binary encrypted to a key that the requesting injector creates for that one request.

## What this protects against, and what it doesn't

It **does** stop:

- downloading the binary from the website, the API, or a shared link;
- grabbing it from network traffic, including through a TLS-intercepting proxy, because the response is encrypted to a key that never leaves the client;
- replaying a captured request: each one uses a single-use challenge;
- installing on unlimited machines: each account has at most 3 registered devices, which the user can revoke;
- copies on the server's disk being useful without the server's payload key.

It **cannot** stop someone who controls the computer running the game from dumping the decrypted binary from memory after injection. Every DRM system has this limit. The client should make it harder (see [Client hardening](#client-hardening)), and the server keeps a delivery log (user, device, version, time) so leaks can be traced and accounts revoked.

## Overview

```
Launchpad                                   MonkeForge
   |  OAuth2 sign-in (scope "account")      ->  bearer access + refresh token
   |  POST /api/v1/injector/devices        ->  register device public key (once per install)
   |  POST /api/v1/injector/challenge      ->  { nonce }                     (valid 60 s, single use)
   |  POST /api/v1/injector/payload        ->  encrypted envelope (binary)
   |  decrypt in memory, verify SHA-256, inject
```

All requests send `Authorization: Bearer <access token>` from an OAuth2 sign-in with the `account` scope (see [SIGN_IN.md](SIGN_IN.md)). Launchpad must be registered as a verified app to get that scope. Bearer requests don't need an `X-CSRF-Token` header.

## 1. Device key (once per install)

Generate an **ECDSA P-256** key pair. Keep the private key in the OS keystore, marked non-exportable where possible (Windows: CNG with the Microsoft Platform Crypto Provider (TPM) if available, otherwise DPAPI-protected). Never write it to disk in plaintext.

```
POST /api/v1/injector/devices
{ "name": "Ben's PC", "public_key": "<base64 SubjectPublicKeyInfo DER>" }
-> 201 { "id": "<device id>", "name": "Ben's PC" }
```

Store the device ID next to the key. Errors: `409` if the key is already registered or the account already has 3 devices. Users manage devices under **Settings -> Devices** (`GET /api/v1/injector/devices`, `DELETE /api/v1/injector/devices/{id}`).

## 2. Challenge

```
POST /api/v1/injector/challenge
-> { "nonce": "<base64url>", "expires_in": 60 }
```

## 3. Payload request

Generate a fresh **ECDH P-256** key pair for this request only (the *ephemeral key*). Then sign the following UTF-8 message with the device key (ECDSA, SHA-256). Fields are joined with `\n` and there is no trailing newline:

```
monkeforge-inject-v1
<nonce>
<device_id>
<guid>
<channel>          e.g. stable
<loader>           bepinex5 | bepinex6 | melonloader
<client_key>       base64 SPKI of the ephemeral public key, exactly as sent
```

The signature may be DER-encoded or raw `r||s` (64 bytes), base64-encoded.

```
POST /api/v1/injector/payload
{ "device_id", "guid", "channel", "loader", "client_key", "nonce", "signature" }
```

| Status | Meaning |
|---|---|
| 200 | Body is the envelope below. Headers: `X-Forge-Guid`, `X-Forge-Version`, `X-Forge-File`, `X-Forge-Sha256` |
| 401 | Nonce expired or reused, device revoked or unknown, or bad signature. Get a new challenge; if it keeps failing, register the device again |
| 402 | The user hasn't bought this mod (or the purchase was refunded or disputed) |
| 403 / 404 | The mod isn't visible to this user, or the channel or loader has no DLL |
| 429 | More than 30 installs in the last hour |

The nonce is consumed even when the request fails, so always fetch a new challenge before retrying.

## 4. Envelope

```
"MFI1"                      4 bytes
key_len                     2 bytes, big-endian
server_public_key           key_len bytes, P-256 SPKI DER (server's ephemeral key)
salt                        32 bytes
iv                          12 bytes
tag                         16 bytes
ciphertext                  rest
```

```
shared = ECDH(client ephemeral private key, server_public_key)    // raw X coordinate, 32 bytes
key    = HKDF-SHA256(ikm = shared, salt = salt, info = "monkeforge-inject-v1", length = 32)
aad    = "monkeforge-inject-v1|<X-Forge-Guid>|<X-Forge-Version>|<X-Forge-File>|<device_id>|<nonce>"   (UTF-8)
dll    = AES-256-GCM-Decrypt(key, iv, ciphertext, tag, aad)
```

Then check that `SHA-256(dll)` equals `X-Forge-Sha256` before injecting. Throw the ephemeral private key away after decrypting, and zero the DLL buffer once it has been loaded.

## Reference client (Python)

This is the same logic the server's end-to-end tests use.

```python
import base64, requests
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

def spki(key):
    return base64.b64encode(key.public_key().public_bytes(
        serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()

def install(http, base, device_id, device_key, guid, channel="stable", loader="bepinex5"):
    nonce = http.post(base + "/api/v1/injector/challenge").json()["nonce"]
    eph = ec.generate_private_key(ec.SECP256R1())
    client_key = spki(eph)
    message = "\n".join(["monkeforge-inject-v1", nonce, device_id, guid, channel, loader, client_key]).encode()
    signature = base64.b64encode(device_key.sign(message, ec.ECDSA(hashes.SHA256()))).decode()

    r = http.post(base + "/api/v1/injector/payload", json={
        "device_id": device_id, "guid": guid, "channel": channel, "loader": loader,
        "client_key": client_key, "nonce": nonce, "signature": signature})
    r.raise_for_status()

    blob = r.content
    assert blob[:4] == b"MFI1"
    klen = int.from_bytes(blob[4:6], "big")
    server_key = serialization.load_der_public_key(blob[6:6 + klen])
    o = 6 + klen
    salt, iv, tag, ct = blob[o:o + 32], blob[o + 32:o + 44], blob[o + 44:o + 60], blob[o + 60:]

    key = HKDF(hashes.SHA256(), 32, salt, b"monkeforge-inject-v1").derive(eph.exchange(ec.ECDH(), server_key))
    h = r.headers
    aad = "|".join(["monkeforge-inject-v1", h["X-Forge-Guid"], h["X-Forge-Version"], h["X-Forge-File"], device_id, nonce]).encode()
    return AESGCM(key).decrypt(iv, ct + tag, aad)   # verify sha256 against X-Forge-Sha256, then inject
```

## Client hardening

These are recommended for Launchpad. The server can't enforce them.

- Decrypt straight into memory and load the assembly from a byte array (for example `Assembly.Load(byte[])` inside the game's Mono runtime). Never write it to a temp file.
- Use certificate pinning for the MonkeForge API.
- Obfuscate the injector itself and check for a debugger before decrypting.
- Re-fetch on every launch instead of caching the DLL. If you must cache, cache the envelope rather than the plaintext. (The current envelope is tied to a one-time ephemeral key, so caching it means keeping that key.)
