# Bass Relay

Bass Relay brings movies and music to your cockpit's bass shakers, with automatic pause when SimHub reports a running game.

If you used the installer, open Bass Relay from the Start menu. For the portable version, extract the files to a permanent writable folder and run BassRelay.exe.
Choose the sound card connected to your shaker amplifier, then adjust strength.
Windows 10/11 x64. No administrator rights or separate .NET install required.

Version 1.5.0 adds custom channel mapping and corrects the output format used for multichannel sound cards. Existing settings keep the same frequency range and send bass to all channels.

To use separate channels, enable **Custom channels** beside the frequency controls and choose **Open mapper**. Select the outputs connected to your shakers and set a frequency range for each. Unchecked channels receive no audio from Bass Relay. Each channel filters the original captured audio independently; vibration strength remains common to the sound card. Choose **Save** to apply the changes or **Cancel** to discard them.

You can keep the sound card configured as 5.1 or 7.1 in Windows and select only the channels you need. Channel names follow the layout reported by Windows. Turning off **Custom channels** restores the common frequency range and output to all channels, retaining your mapping for later. Selecting no channels silences that card in custom mode. Frequency limits remain 0–200 Hz, with the lower limit below the upper limit.

SimHub auto-pause is enabled by default. Bass Relay pauses all its output while SimHub reports a running game, including the quiet gaps between effects. When that status ends, output resumes unless you have paused manually. You can turn auto-pause off in Settings. SimHub's local web server must be enabled (default port 8888).

For some games, auto-pause follows entering and leaving the track. If the connection to SimHub drops, output resumes after about five seconds. Manual pause stays in effect until you resume it yourself.

Settings and logs are created in Data beside the executable. Before deleting
this portable copy, disable startup with Windows and exit from the tray menu.

Instructions and source: https://github.com/Lord-of-the-Bots/BassRelay
Releases: https://github.com/Lord-of-the-Bots/BassRelay/releases/latest

Keep THIRD-PARTY-NOTICES.txt and Licenses with the application.

## Support Bass Relay

If Bass Relay adds something to your setup, you can support its development with a crypto donation:

BTC (Bitcoin):
1NbtPNkofnKZRjLpULRjhKuAtbh12DovC9

USDT, TRX (TRC20):
TUgM6hPokF1vPUW8CRp77CgvF3YroabwFP

TON:
UQBLdOWJeVeVg4b0-HkQGNVV8HG6-xWS7moZOUfNBz2-Jf3u

ETH (ERC20):
0x14bba7b8b76ea4743a202bdee2144e4d558ddf93

LTC (Litecoin):
LRRS5YBeqfkYpw2jC2bDAWgpcgm7Wpu6pM
