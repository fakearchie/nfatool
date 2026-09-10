<div align="center">

# nfa.pub tool

**Steam cache login for Windows. Switch accounts instantly, with no browser and no passwords.**

[![Version](https://img.shields.io/badge/version-v0.4.0-blue?style=flat-square)](https://archievable.shop)
[![Build](https://img.shields.io/badge/build-automated-2ea44f?style=flat-square&logo=githubactions&logoColor=white)](https://archievable.shop)
[![Platform](https://img.shields.io/badge/Windows-x64-0078D6?style=flat-square&logo=windows11&logoColor=white)](https://archievable.shop)
[![Tauri](https://img.shields.io/badge/Tauri-2-FFC131?style=flat-square&logo=tauri&logoColor=white)](https://tauri.app)
[![Discord](https://img.shields.io/badge/Download-Discord-5865F2?style=flat-square&logo=discord&logoColor=white)](https://archievable.shop)

[Website](https://archievable.shop) · [Support](https://archievable.shop)

</div>

---

Paste your token, pick an account, and you're in. nfa.pub tool writes it straight into Steam's local cache and starts the client.

## Use it

1. **Add accounts.** Paste your codes, load a `.txt`, or pull in every account already signed in on this PC.
2. **Sign in.** Click a tile for its details, then sign in. Steam starts already logged in.
3. **Keep it tidy.** Colour tags, search, cooldown timers, and last-used times.

Closes to the system tray, and you can switch accounts from there too.

## Good to know

- **Lock it with a password.** Optional. Your login codes get encrypted with a key derived from it (Argon2id + AES-256-GCM), and the app asks for it on every start. You get a recovery code when you set one. Keep it, because there is no reset.
- **Login codes are encrypted at rest** even without a password, using Windows DPAPI, so a copied data file is useless on any other machine or user account.
- **Streamer mode** hides usernames and Steam IDs, and the window can be excluded from OBS, Discord and screenshots entirely.
- **Steam Web API key** (optional) adds level and ban info to each account. Without one you still get online status.
- **CS2 extras.** Shared launch options, copy your settings onto every alt, skip Workshop re-downloads, launch the game on sign-in.
- **Updates install themselves.** On start it asks GitHub whether a newer release exists and offers to install it. Every update is signed, and a build that fails the signature check is refused. Turn the check off in Settings if you would rather do it by hand.
- **Nothing else phones home.** Only two other things ever leave the PC: avatar images from Steam's CDN, which you can turn off, and account lookups, which happen only if you add a Web API key. Your login codes never leave.
