# slip

Keep your Windows PC awake without keeping the monitor on.

`slip` blocks system sleep/standby (via `SetThreadExecutionState`) so
background work — downloads, renders, scripts, servers — keeps running.
It deliberately does **not** touch display power settings: your monitor
still turns off on whatever schedule you've already set in Windows, since
there's no reason to burn a screen for a background task.
If you *do* want the screen to stay up (watching a long build, a
dashboard, a presentation), add `-m`: `slip -m 2` also keeps the monitor
on and stops Windows from idle-locking the session.

Optionally controllable remotely over Telegram, with an inline-button
dashboard, so you can extend/cancel the awake window from your phone.

## Install

Grab the latest zip from [Releases](../../releases), extract it anywhere,
and run `install.bat`. It adds that folder to your user `PATH` and opens a
test window running `slip help`. Open a **new** terminal afterward — `PATH`
changes don't apply to windows already open.

Or build from source:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

## Usage

```
slip                    Show status (same as "slip status")
slip <hours>            Stay awake for N hours    (e.g. slip 4, slip 90m, slip 2d)
slip -d <days>          Stay awake for N days     (e.g. slip -d 4)
slip until <HH:mm>      Stay awake until a clock time (e.g. slip until 23:30)
slip while <process>    Stay awake while a process runs (e.g. slip while ffmpeg)
                        Add a duration to cap it: slip while ffmpeg 6
slip forever            Stay awake until 'slip off'
slip +<hours>           Extend the current run (e.g. slip +2, slip +30m)
slip off                Cancel - restore normal sleep behavior
slip status             Show whether it's currently active
slip requests           Show what's keeping the PC/monitor awake (powercfg /requests)
slip help               Show this help
```

Options, combinable with any of the above:

```
-m                      Also keep the monitor on and block the idle lock screen
-then <action>          When the run finishes: sleep, hibernate or shutdown
```

Examples:

```
slip -m 2                            watch a long build without the screen going dark
slip while ffmpeg -then shutdown     turn the PC off once the render is done
slip until 7:00 -then sleep          stay up for the overnight download, sleep in the morning
```

`while` takes a process name as shown in Task Manager's Details tab
(`.exe` optional) or a PID; the run ends once no matching process is left.

`-then` never fires instantly: when the run finishes, slip waits 60 seconds
first. `slip off` (or the Telegram ✋ Cancel button) during that window
cancels the action. It only fires when a run finishes on its own, never on
`slip off`.

`slip requests` works from any terminal: `powercfg /requests` needs admin,
so from a normal one slip asks for a single UAC confirmation and prints the
result right there. The first line says what slip itself is holding, e.g.
`slip is holding SYSTEM + DISPLAY (monitor kept on)`.

If Telegram is linked, the bot also messages you 10 minutes before a timed
run ends (with ➕ extend buttons) and when a run finishes.

## Telegram remote control (optional)

1. Create a bot with [@BotFather](https://t.me/BotFather), grab the token.
2. Run `slip -telega "your_token"`.
3. Message your bot anything. `slip` will ask, in the terminal, whether the
   Telegram user who just messaged it is you (`y`/`n`). Say `y` and that
   Telegram account becomes the only one the bot will ever respond to -
   everyone else is silently ignored.
4. You'll get a message back with an inline-button dashboard: pick a
   duration, or turn it off, right from the buttons. Typed commands
   (`4`, `off`, `status`) work too.
5. `slip -telega reset` unlinks the bot and erases the saved token. To
   reconnect later, run `-telega "token"` again and repeat the same
   confirm-your-identity flow - there's no shortcut back in.
6. `slip -telega status` shows whether the bot is linked and whether its
   background daemon is actually running.

The CLI and the Telegram bot read/write the same local state, so starting
from one and checking/cancelling from the other just works.

### If the bot goes quiet

The Telegram daemon is a plain background process - it does **not**
survive a PC reboot or sign-out on its own (no scheduled task, nothing
added to Windows startup). If it dies, `slip` notices and restarts it the
next time you run *any* `slip` command (including a plain `slip status`),
so linking survives - you never need to `reset` and re-link just because
the daemon died. To restart it immediately without waiting for that,
run `slip -telega start`.

## How it works / limits

- Uses `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` in a small background
  process that self-terminates when the timer runs out (or is killed by
  `slip off`). No scheduled tasks, no registry changes.
- The bot token is stored locally in `%LOCALAPPDATA%\slip\telegram.json`,
  unencrypted. It never leaves your machine except in calls to the Telegram
  Bot API. If you want it encrypted at rest, that's a reasonable thing to
  add — open an issue.
- Windows only.
