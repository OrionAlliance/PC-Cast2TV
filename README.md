<h1 align="center">PC Cast2TV</h1>

<p align="center">
  <b>Cast your Windows PC's screen and sound to your TV or Roku, over your home network.</b><br>
  <b>Roku, Chromecast and smart TVs. Press Connect, pick your device, press Cast. That's the app.</b>
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

I wanted my PC screen to show on my TV. The whole screen, with sound, smooth, without running an HDMI cable across the room or buying another gadget. The ways I tried either lagged, stuttered, or needed extra hardware. So with the help of Claude AI, I built exactly what I wanted: one small app, one button, and the TV shows what my PC shows. Hopefully it's what you've wanted, too.

## What it does

- **Casts your main screen and your PC's sound** at up to 1080p and 60 frames a second. Bigger screens are scaled down to fit.
- **Uses your graphics card's video encoder** (NVIDIA, AMD or Intel), so casting barely touches your processor. If the card's encoder won't start, the processor takes over at 30 frames a second.
- **Casts to:**
  - **Rokus:** Roku streaming sticks and boxes, and Roku TVs (through the free DisplayHUB channel, which the app helps you add).
  - **TVs with Chromecast built in:** many VIZIO, Sony, TCL and Hisense TVs, plus Chromecast dongles and Google TV boxes. No extra app needed.
  - **Other smart TVs** that can play video from your home network (DLNA).

  Not sure about yours? Just press Detect. If your TV shows up in the list, the app can cast to it.
- **One portable exe.** No installer, no extra programs, nothing running in the background. Its settings live in a `Settings` folder next to the app.
- **Keeps itself up to date.** When a new version is out, the app offers it when you open it. One click and it updates and restarts itself. It never interrupts a cast.
- **A health readout** while you cast: dropped frames, CPU load and GPU load. The numbers stay up after the cast so you can see how it went.
- **8 color themes,** plus Random and your own color, from the ⚙ cog. Settings also shows your version and links back to this page.

## Requirements

- **Works on Windows 10 and Windows 11** (64-bit). It looks best on Windows 11, where the title bar picks up your theme color and the window gets rounded corners.
- **The PC and the TV on the same home network.** Windows has to call that network Private, not Public. The app tells you if it doesn't.
- **Running a VPN?** If you're having connection issues, you may need to turn on its "Allow LAN" setting (some VPNs call it "Local network sharing") so the PC can reach the TV.

## Installing

1. Download **`PC-Cast2TV.exe`** from the [Releases](../../releases) page. That's the only official download. Copies posted anywhere else aren't from me, so don't run them.
2. Put it in its own folder (it keeps its `Settings` folder next to it) and run it. **Windows will show a blue "Windows protected your PC" warning.** That's SmartScreen reacting to an unsigned open-source app with no download reputation yet, not a threat detection. Click **More info → Run anyway**. (Don't take my word for what the app does. The entire source code is this repository.)
3. Press **Connect**. The first time, Windows asks once to let your TVs and Roku reach the app. That's a firewall rule for this app only, on private networks only.
4. Press **Detect** to find your TVs and Roku, click the one you want, and press **Cast**.

## Roku: one free channel, and the app adds it for you

A Roku plays your PC through **DisplayHUB**, a free channel in the Roku Channel Store. You don't have to go looking for it:

<p align="center">
  <img src="Images/displayhub.png" width="290" alt="The DisplayHUB channel icon" /><br>
  <i>DisplayHUB on your Roku's home screen.</i>
</p>

1. Click your Roku in PC Cast2TV's list.
2. If DisplayHUB isn't on it yet, PC Cast2TV pops up and shows you. Press **ADD DISPLAYHUB** and its page opens on your TV.
3. Press **OK** on your Roku remote. PC Cast2TV sees it arrive, the popup closes by itself, and you're ready to press **Cast**.

**Good to know:** with DisplayHUB, the Roku remote can't pause, play or skip on your PC.

PC Cast2TV's own Roku app is coming soon to the Roku Channel Store. With it, the Roku remote controls your PC too. Once it's in the store, PC Cast2TV will add it for you the same way, with one OK on the remote.

## What it's been tested on

So far it has run on:

- Two desktop PCs on Windows 11, both wired to the network: one with an AMD Radeon card, one with an NVIDIA RTX 5060 Ti.
- A Roku Ultra (Roku OS 15.3), on Wi-Fi, through DisplayHUB.
- A VIZIO TV with Chromecast built in, wired.

Not tested yet:

- **Laptops,** especially ones with two graphics chips.
- **Intel graphics** (the app supports Intel's video encoder, but no Intel card has been tested).
- **A Windows 10 PC.**
- **A PC on Wi-Fi.** (The Roku on Wi-Fi works fine.)
- **DLNA smart TVs.** The app supports them, but none have been tested.
- **Other Rokus** (Roku TVs, Express, Streaming Stick) and **Chromecast dongles or Google TV boxes.**

If it works, or doesn't, on your setup, please open an [Issue](../../issues) with your `debug.log`. That's how this list gets shorter.

## FAQ

**Why is the TV a few seconds behind my PC?**
Roku and Chromecast play the cast in one-second pieces and keep a few of them in hand, so a Wi-Fi hiccup never freezes the picture. That's a few seconds behind, and it's the trade for a smooth picture.

**Why did my screen switch to 60 Hz?**
A 75 or 144 Hz screen copied 60 times a second can't land evenly on a 60 Hz TV, which shows as a stutter. So while you cast, the app runs your screen at 60 Hz, and it puts your screen back the way it was when the cast ends.

**It says the TV isn't answering.**
Make sure the TV (or the Roku) is on and on the same home network as the PC. If you run a VPN, turn on its "Allow LAN" setting. If the TV moved to a new address, press Detect to find it again.

**My Roku won't start the cast, or ADD DISPLAYHUB didn't open anything.**
PC Cast2TV talks to a Roku the same way Roku's own phone app does. On the Roku, open **Settings > System > Advanced system settings > Control by mobile apps** and make sure it is turned on (enabled).

**Can I hide a device I don't use?**
Right-click it in the list to hide it from the list or forget it from Detect. Right-click Detect to bring hidden devices back.

## Your data stays yours

This app collects **nothing** and sends **nothing** about you: no telemetry, no analytics, no accounts. It talks to your TVs on your home network, and its only internet use is asking this repository whether a newer version is out (and downloading it if you say so). That request sends nothing about you or your PC.

## Found a bug?

Open an [Issue](../../issues). Include the `debug.log` file from the `Settings` folder next to the app. It records what the app was doing, how each cast went, and your TV's model and software version. It contains no personal information: user names and folder paths are swapped for short tags. Read it yourself first if you like.

---

**© 2026 TOA. Released under the [GNU GPLv3](LICENSE).** It's free forever, and every fork must stay free and open source too.

This software is provided "as is", without warranty of any kind, express or implied. You use it at your own risk. TOA is not liable for any damage to hardware, loss of data, or any other damages arising from the use of this software.
