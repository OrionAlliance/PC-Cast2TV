<h1 align="center">PC Cast2TV</h1>

<p align="center">
  <b>Cast your Windows PC's screen and sound to your TV, over your home network.</b><br>
  <b>Roku, Chromecast and smart TVs. Press Connect, pick your TV, press Cast. That's the app.</b>
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/badge/%E2%AC%87%EF%B8%8F%20Download-Latest%20version-2ea44f?style=for-the-badge" alt="Download the latest version"></a>
  &nbsp;&nbsp;&nbsp;
  <a href="../../releases"><img src="https://img.shields.io/github/downloads/OrionAlliance/PC-Cast2TV/total?style=for-the-badge&logo=github&color=2ea44f" alt="Total downloads"></a>
  &nbsp;&nbsp;&nbsp;
  <a href="https://ko-fi.com/orionailliance"><img src="https://img.shields.io/badge/%E2%9D%A4%EF%B8%8F%20Donate-Ko--fi-ff5e5b?style=for-the-badge" alt="Donate on Ko-fi"></a>
</p>

---

<p align="center">
  <img src="Screenshots/main-window.png" width="760" alt="PC Cast2TV" />
</p>

## Why this exists

I wanted my PC on my TV. The whole screen, with sound, smooth, without running an HDMI cable across the room or buying another gadget. The ways I tried either lagged, stuttered, or needed extra hardware. So with the help of Claude AI, I built exactly what I wanted: one small app, one button, and the TV shows what my PC shows. Hopefully it's what you've wanted, too.

## What it does

- **Casts your main screen and your PC's sound** at up to 1080p and 60 frames a second. Bigger screens are scaled down to fit.
- **Uses your graphics card's video encoder** (NVIDIA, AMD or Intel), so casting barely touches your processor. If the card's encoder won't start, the processor takes over at 30 frames a second.
- **Works with Roku** (through the free PC Cast2TV Roku app below), **Chromecast and Google TV**, and **DLNA smart TVs**.
- **One portable exe.** No installer, no extra programs, nothing running in the background. Its settings live in a `Settings` folder next to the app.
- **Your Roku remote works the PC:** Play/Pause, Next and Previous control whatever is playing on the PC, Fast Forward and Rewind send the arrow keys, and Back ends the cast.
- **A health readout** while you cast: dropped frames, CPU load, GPU load, and how far behind live the TV is. The numbers stay up after the cast so you can see how it went.
- **8 color themes,** plus Random and your own color, from the ⚙ cog.

## Requirements

- **Windows 10 or 11, 64-bit.**
- **The PC and the TV on the same home network.** Windows has to call that network Private, not Public. The app tells you if it doesn't.
- **Running a VPN?** Turn on its "Allow LAN" setting (some VPNs call it "Local network sharing") so the PC can reach the TV.

## Installing

1. Download **`PC Cast2TV.exe`** from the [Releases](../../releases) page. That's the only official download. Copies posted anywhere else aren't from me, so don't run them.
2. Put it in its own folder (it keeps its `Settings` folder next to it) and run it. **Windows will show a blue "Windows protected your PC" warning.** That's SmartScreen reacting to an unsigned open-source app with no download reputation yet, not a threat detection. Click **More info → Run anyway**. (Don't take my word for what the app does. The entire source code is this repository.)
3. Press **Connect**. The first time, Windows asks once to let your TVs reach the app. That's a firewall rule for this app only, on private networks only.
4. Press **Detect** to find your TVs, click the one you want, and press **Cast**.

## Roku: install the PC Cast2TV Roku app

A Roku plays your PC through its own PC Cast2TV app. Until that app is in the Roku Channel Store, it installs through Roku's developer mode, which is free and built into every Roku:

1. Download **`PC-Cast2TV-Roku-v0.1.11.zip`** from the [Releases](../../releases) page.
2. Turn on developer mode. On the Roku remote, press **Home 3 times, Up 2 times, then Right, Left, Right, Left, Right**. Accept the agreement, set a password, and let the Roku restart.
3. Open the Roku's IP address in a browser, upload the zip, and the PC Cast2TV app appears on the Roku. The browser asks you to sign in first: the user name is `rokudev` and the password is the one you just set. (The Roku shows its IP address on the developer mode screen, and anytime under **Settings > Network > About**.)

**Good to know:** a Roku holds one developer app at a time, so installing this one replaces any other developer app on it.

## FAQ

**Why is the TV a few seconds behind my PC?**
Roku and Chromecast play the cast in one-second pieces and keep a few of them in hand, about 5 to 6 seconds on a Roku, so a Wi-Fi hiccup never freezes the picture. The health readout shows exactly how far behind it is.

**Why did my screen switch to 60 Hz?**
A 75 or 144 Hz screen copied 60 times a second can't land evenly on a 60 Hz TV, which shows as a stutter. So while you cast, the app runs your screen at 60 Hz, and it puts your screen back the way it was when the cast ends.

**It says the TV isn't answering.**
Make sure the TV (or the Roku) is on and on the same home network as the PC. If you run a VPN, turn on its "Allow LAN" setting. If the TV moved to a new address, press Detect to find it again.

**Can I hide a device I don't use?**
Right-click it in the list to hide it from the list or forget it from Detect. Right-click Detect to bring hidden devices back.

## Your data stays yours

This app collects **nothing** and sends **nothing** anywhere: no telemetry, no analytics, no accounts. It never uses the internet at all. It only talks to your TVs on your home network.

## Found a bug?

Open an [Issue](../../issues). Include the `debug.log` file from the `Settings` folder next to the app. It records what the app was doing, how each cast went, and your TV's model and software version. It contains no personal information: user names and folder paths are swapped for short tags. Read it yourself first if you like.

---

**© 2026 TOA. Released under the [GNU GPLv3](LICENSE).** It's free forever, and every fork must stay free and open source too.

This software is provided "as is", without warranty of any kind, express or implied. You use it at your own risk. TOA is not liable for any damage to hardware, loss of data, or any other damages arising from the use of this software.
