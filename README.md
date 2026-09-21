<div align="center">

<img src="NfaLoader/Assets/Logo.png" width="96" alt="nfa.pub Loader">

# nfa.pub Loader

**Sign in to Steam with a login token, and buy or replace nfa.pub accounts from the same window.**

[![Version](https://img.shields.io/badge/version-v0.5.0-blue?style=flat-square)](https://github.com/fakearchie/nfatool/releases/latest)
[![Build](https://img.shields.io/badge/build-automated-2ea44f?style=flat-square&logo=githubactions&logoColor=white)](https://github.com/fakearchie/nfatool/actions)
[![Platform](https://img.shields.io/badge/Windows-x64-0078D6?style=flat-square&logo=windows11&logoColor=white)](https://nfa.pub)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Discord](https://img.shields.io/badge/Download-Discord-5865F2?style=flat-square&logo=discord&logoColor=white)](https://archievable.shop)

[Website](https://nfa.pub) · [Download](https://github.com/fakearchie/nfatool/releases/latest) · [API docs](https://nfa.pub/docs)

</div>

---

Paste a login token and Steam starts already signed in. No password, no authenticator.

## Use it

1. **Sign in.** Paste a token, or a whole `steamid----token` line, and click Sign in.
2. **Buy.** Pick a CS2 account type, check the price and your balance, and buy. The account lands in History, ready to sign in.
3. **Replace.** If a bought account stops working, click Replace. nfa.pub checks it and sends a working one in its place.

## Good to know

- **Account card.** Premier rating, CS2 level, cooldown and VAC status, shown with the game's own badges and refreshed on demand.
- **History.** Every account you use is kept, searchable, sortable into groups, and importable in bulk.
- **Replacements.** 3 per order, within 6 hours of delivery. The card shows the time left, and a check that finds the account still working uses none.
- **Purchases are never lost.** An interrupted purchase is kept and can be finished later without paying twice.
- **Loadouts.** Build a T and CT loadout once and apply it to any account without launching CS2.
- **Clear Workshop.** Unsubscribe an account from every CS2 Workshop item before it downloads gigabytes on first launch.

## Your nfa.pub API key

Buying and replacing need a key. Create one on your account page at [nfa.pub](https://nfa.pub) and paste it into **Settings**. The key spends your balance, so it is stored encrypted with Windows DPAPI and only your Windows account on this PC can read it.

## Requirements

Windows 10 1809 or later, with Steam installed. Grab the installer from [Releases](https://github.com/fakearchie/nfatool/releases/latest) or Discord, run it, and open the app. It checks for updates on start.
