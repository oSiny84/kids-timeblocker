# TimeBlocker

[한국어](README.md) | [English](README.en.md)

A Windows 10 / 11 program that **blocks access to YouTube / Roblox on a weekly schedule**.

The administrator uses **a Telegram chat as a remote console** — checking status, changing the
blocking schedule, and granting temporary allowances when needed. There is no settings GUI on
the blocked PC itself.

```
        [Admin's phone]
               │
           Telegram
               │
               ▼
      ┌─────────────────┐
      │ Windows Service │
      ├─────────────────┤
      │ Telegram Client │
      │ Command Parser  │
      │ Command Handler │
      │ ScheduleManager │
      │ PermitManager   │
      │ Policy Engine   │
      │ DNS Block       │
      │ Firewall Block  │
      └─────────────────┘
               │
               ▼
        YouTube / Roblox
          access control
```

The PC opens no inbound ports. The service only does **outbound long polling** to Telegram.

---

## ⚡ Quick Install (for parents, 5 minutes)

**Just follow this order to install on your child's PC.** (Build/development details start at
"1. Project structure" below.)

### 1) Get two things

| What | Where |
|---|---|
| **.NET 8 Desktop Runtime** | [Download page](https://dotnet.microsoft.com/download/dotnet/8.0) → click **"Desktop Runtime" x64** |
| **TimeBlocker installer** | [Latest release](https://github.com/oSiny84/kids-timeblocker/releases/latest) → download `TimeBlocker-*.zip` |

### 2) Create a Telegram bot + find your User ID (one-time setup)

1. In the Telegram app, search **@BotFather** → send `/newbot` → follow the prompts to pick a
   name and you'll get a **Bot Token**
   (format: `123456789:AAE-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`, copy it somewhere)
2. Search for the bot you just made and send it any message (e.g. `/start`)
   — skip this and step 3 will show nothing
3. Find your **numeric User ID** — replace `<TOKEN>` below with the Bot Token from step 1
   and paste it into your **web browser's address bar**, then hit Enter

   ```
   https://api.telegram.org/bot<TOKEN>/getUpdates
   ```

   The **number** inside something like `"from":{"id":123456789,...` on the page is your User ID.
   (Third-party bots like `@userinfobot` sometimes don't respond or are blocked, so we use this
   method straight from Telegram itself. If a terminal is more comfortable,
   `curl "https://api.telegram.org/bot<TOKEN>/getUpdates"` does the same thing.)

### 3) Install

1. Unzip the downloaded file
2. Double-click **`install.bat`**
   - If a "publisher could not be verified" warning appears, choose **"Yes" / "Run"**
   - When asked for the Bot Token and User ID, paste the values from step 2
3. Once installation finishes, a diagnostic check (`doctor`) runs automatically

### 4) Confirm it worked

Send this to the bot you just made, over Telegram:

```
status
```

If you get a reply, it worked. If something looks off, send `doctor` to see the cause and how
to fix it.

### 5) Commands you'll use most

This is really all you need for day-to-day use. Swap the target for `youtube` / `roblox` / `shorts` / `all`.

| Command | What it does |
|---|---|
| `status` | Show current status (blocked or not, and why) |
| `auto youtube` | YouTube follows the schedule (default) |
| `block youtube` | Keep YouTube blocked regardless of schedule |
| `unblock youtube` | Keep YouTube open regardless of schedule |
| `youtube 30` | Allow for 30 minutes (automatically reverts afterward) |
| `schedule mon-fri 21:00 07:00` | Block weeknights 9pm to 7am |
| `block shorts` | Block Shorts only (regular YouTube videos still work) |
| `msg have dinner first` | Pop a message up on the PC screen |
| `help` (or `list`) | Full command list |

`block shorts` requires **enabling browser policy in the configuration first**
(see "Blocking Shorts only" in section 10). If you issue the command without it,
the reply tells you so.

For more commands and examples, see the **"8. Telegram commands"** section below.

> **Please make your child's account a "Standard user".** An administrator account can stop the
> service directly, which defeats the purpose. (Settings → Accounts → Family & other users)

---

## 1. Project structure

```
TimeBlocker.sln
├─ src/TimeBlocker.Shared          Shared library (close to Windows-independent)
│  ├─ Models/                      BlockTarget, DaySchedule, TemporaryPermit, AccessDecision
│  ├─ Configuration/               TimeBlockerConfig, AppPaths, JsonConfigurationStore
│  ├─ Core/                        ScheduleManager, TemporaryPermitManager, AccessPolicyEngine
│  ├─ Remote/                      RemoteCommandParser, RemoteCommandHandler, ResponseFormatter
│  ├─ Ipc/                         IPC contracts + Named Pipe client
│  └─ Common/                      JsonUtil, SecretProtector(DPAPI), PasswordHash, ISystemClock
│
├─ src/TimeBlocker.Service         Windows Service (the core)
│  ├─ Worker/                      EnforcementWorker (enforcement loop), RemoteProviderWorker
│  ├─ Blocking/                    HostsFileManager, FirewallManager, RobloxLocator,
│  │  └─ Dns/                      DnsProxyServer, DnsMessage, DnsSelfTest,
│  │                                NetworkAdapterDnsConfigurator, AdapterDnsStateStore
│  ├─ Remote/                      IRemoteCommandProvider, TelegramRemoteCommandProvider
│  ├─ Ipc/                         NamedPipeIpcServer, NotifierHub (tray app channel)
│  ├─ Logging/                     File logging + rotation
│  ├─ Security/                    DataDirectoryHardener (ACL)
│  ├─ Maintenance/                 DoctorService (diagnostics), SystemRestoreService (restore)
│  └─ Setup/                       set-token / set-admin / cleanup etc. local setup commands
│
├─ src/TimeBlocker.Admin           Local admin CLI (helper tool, no GUI)
├─ src/TimeBlocker.Notifier        Tray app that shows on-screen alerts and replies (user session)
├─ tests/TimeBlocker.Tests         xUnit unit tests
└─ scripts/                        Build / install / uninstall / Telegram setup / smoke test
```

### Key design decisions

| Item | Detail |
|---|---|
| **Single source of truth** | "Is this blocked right now?" is decided in exactly one place: `AccessPolicyEngine`. DNS / firewall / Telegram / CLI never decide on their own — they only use this result. |
| **Decision priority** | Target `Open` (unblock) → ALLOW · Temporary permit active → ALLOW · Target `Blocked` (block) → BLOCK · Inside the blocking schedule → BLOCK · otherwise → ALLOW |
| **Loose coupling** | If Telegram is down, `EnforcementWorker`'s blocking keeps working. They run as separate `BackgroundService` instances. |
| **Expiry is absolute** | Temporary permits are stored as a **UTC absolute expiry timestamp**, not "minutes remaining". They don't grow across reboots, restarts, or clock changes. |
| **Remote is outbound-only** | No inbound port on the PC. The Named Pipe is local-only, ACL'd to administrators only. |

---

## 2. Requirements

- Windows 10 / Windows 11
- .NET 8 (current LTS)
  - To build: .NET SDK 8.0 or later (SDK 9 also builds it)
  - To run: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — or publish self-contained
- Administrator privileges (for install and configuration)
- No paid third-party libraries (NuGet packages are official Microsoft packages only)

---

## 3. Building

### Visual Studio

Open `TimeBlocker.sln` and build.

### Command line

```powershell
dotnet build TimeBlocker.sln -c Release
dotnet test  tests\TimeBlocker.Tests\TimeBlocker.Tests.csproj
```

### Build and publish in one step

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

### Even simpler: double-click a .bat file

If typing PowerShell commands is a hassle, use the `.bat` files at the repo root.
Ones that need admin rights **prompt for elevation automatically when run.**

| File | What it does | Elevation |
|---|---|---|
| `build.bat` | Build + test + publish | Not needed |
| `install.bat` | Asks for Bot Token / User ID, then installs; runs doctor automatically at the end | Automatic |
| `doctor.bat` | Full status check | Automatic |
| `dns-restore.bat` | **Emergency recovery when the internet is down** | Automatic |
| `smoke-test.bat` | Integration smoke test | Automatic |
| `configure-telegram.bat` | Change Telegram settings | Automatic |
| `uninstall.bat` | Remove + restore | Automatic |

If the `publish` folder doesn't exist, `install.bat` asks whether to build first.

### Building a distributable package

Creates a zip to install on another PC (e.g. your child's PC).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1
```

Or just run `make-release.bat`.

This produces `release\TimeBlocker-<version>.zip`, containing the executables, install scripts,
`.bat` launchers, README, and a quick-start text file — **source code and build tools are not
included.**

The target PC needs no .NET SDK — only the **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)**.
Unzip it and run `install.bat`.

The `publish\` folder contains `TimeBlocker.Service.exe`, `TimeBlocker.Admin.exe`, and
`TimeBlocker.Notifier.exe`.

---

## 4. Installing

Run in an **Administrator PowerShell**.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 `
    -BotToken "123456789:AAE..." `
    -AdminUserId 123456789
```

What the script does:

1. Copies files to `%ProgramFiles%\TimeBlocker`
2. Sets ACLs on the install folder (only SYSTEM / Administrators can write, Users can only read)
3. Encrypts the Bot Token with DPAPI and saves it, registers the admin User ID, enables Telegram
4. Registers the service with `sc.exe` — **auto-start**, LocalSystem account
5. Applies service recovery settings
6. Starts the service

### Service recovery settings

The install script applies the following (visible in the service's Properties → Recovery tab):

| Item | Value |
|---|---|
| First failure | Restart the Service (after 10s) |
| Second failure | Restart the Service (after 30s) |
| Subsequent failures | Restart the Service (after 60s) |
| Reset fail count after | 1 day |
| Recover on nonzero exit codes too | Yes (`failureflag 1`) |

```powershell
sc.exe failure TimeBlocker reset= 86400 actions= restart/10000/restart/30000/restart/60000
sc.exe failureflag TimeBlocker 1
```

**Why this matters:** in DNS proxy mode, if the service stays dead, the adapter's DNS can be
left pointing at 127.0.0.1. When the service auto-restarts, it detects the unclean shutdown at
startup and restores the adapter's DNS.

Check the current setting:

```powershell
sc.exe qfailure TimeBlocker
```

To set the Token / User ID later, install without arguments first, then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\configure-telegram.ps1 `
    -BotToken "123456789:AAE..." -AdminUserId 123456789
```

### Installing / removing the Windows Service manually

```powershell
# Install
sc.exe create TimeBlocker binPath= "\"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe\"" start= auto obj= LocalSystem
sc.exe failure TimeBlocker reset= 86400 actions= restart/10000/restart/30000/restart/60000
sc.exe start TimeBlocker

# Status
sc.exe query TimeBlocker

# Remove (always run cleanup first)
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" cleanup
sc.exe stop   TimeBlocker
sc.exe delete TimeBlocker
```

### Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
# To also remove settings/logs
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1 -RemoveData
```

### Uninstall guarantees full restoration

Uninstall and `cleanup` share the **same code** (`SystemRestoreService`). The order is:

```
1. Clear temporary permits / state
2. Restore saved Original DNS      <- restore internet first
3. Remove the TimeBlocker hosts region
4. Remove TimeBlocker firewall rules
5. Restore browser policy
6. Flush DNS cache
7. Remove the Windows Service
8. Remove remaining state files
```

**If a step fails, the rest still run**, and a full result summary is printed at the end.

```
Permit cleanup         : OK
DNS restore            : OK (1: Wi-Fi)
Hosts cleanup          : OK
Firewall cleanup       : OK (1 removed)
Browser policy restore : OK
DNS cache flush        : OK
State cleanup          : OK
Service removal        : OK

System restored successfully.
```

If something fails, the fix is shown alongside it, and the script exits with code `2`.

```
DNS restore       : FAILED: some adapters failed to restore (Wi-Fi)
Hosts cleanup     : OK
Firewall cleanup  : OK

Suggested fix:
- DNS restore: In network settings, set that adapter's DNS back to "Obtain DNS server address automatically".

System partially restored. 1 step(s) failed - see the fix above.
```

Safety rules:

- hosts is edited **only within the `# TIMEBLOCKER BEGIN ~ END` marker region.** Existing
  content is never touched.
- The firewall only removes **our own rules**, named starting with `TimeBlocker_`. Your other
  rules are left alone.
- DNS is restored to the **saved original value**. It never unconditionally switches to DHCP, so
  a manually-configured DNS environment keeps its setting.
- If adapter DNS restoration isn't complete, the state file (backup) is **not deleted**, so the
  recovery information isn't lost.

---

## 5. Why administrator privileges are needed

| Action | Reason |
|---|---|
| Editing the hosts file | Only administrators can write to `C:\Windows\System32\drivers\etc\hosts`. |
| Creating/deleting firewall rules | `netsh advfirewall` requires administrator rights. |
| Binding the DNS proxy to port 53 | Ports below 1024 + changing the network adapter's DNS. |
| Setting ACLs on the data folder | Preventing a regular user from editing settings/permits requires ACL-change rights. |
| Registering the Windows Service | Registering/starting a service requires administrator rights. |
| Flushing the DNS cache | Manipulating the system DNS resolver cache. |

The service runs under the **LocalSystem** account, which is why it can do the above. There is
no GUI, and the local CLI can only connect to the service if it, too, is running elevated.

It does not bypass Windows security features or hide the process. It shows up normally in the
service list and Task Manager.

---

## 6. Setting up the Telegram Bot

1. Send **@BotFather** on Telegram a `/newbot` message to create a bot and receive a **Bot Token**.
   (format: `123456789:AAE-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`)
2. **Start a 1:1 chat** with the bot you made first. (Send `/start` once so the bot can receive
   messages from you)
3. Find your **numeric User ID**.
   - It must be the **numeric ID**, not a username. Usernames can change and aren't used for
     authentication.
   - Most reliable method: after messaging the bot in step 2, open
     `https://api.telegram.org/bot<TOKEN>/getUpdates` in a browser or with `curl` and read the
     number from `"from":{"id":...}`.
     (`<TOKEN>` is the Bot Token from step 1.) Third-party bots like `@userinfobot` can also
     tell you this, but sometimes don't respond — the method above is more reliable.
4. Configure it on the PC with administrator rights.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\configure-telegram.ps1 `
    -BotToken "123456789:AAE..." -AdminUserId 123456789
```

### Security

- The Bot Token is stored **encrypted with DPAPI (LocalMachine)**. It never sits in the config
  file in plain text.
- The Token is **never written to any log.**
- Messages from anyone other than an allowed numeric User ID are ignored, and only this is
  recorded:
  ```
  Unauthorized Telegram access attempt UserId=xxxx
  ```
- **The Bot Token and admin User ID cannot be changed via Telegram commands.** They can only be
  changed at the PC itself with administrator rights. (`admin list` can only view them.)

---

## 7. First run

The install script starts the service, so no separate step is needed. Just verify it.

```powershell
sc.exe query TimeBlocker
```

Send this to the bot over Telegram:

```
status
```

A reply means it's working.

```
TimeBlocker STATUS

PC          : ONLINE
Service     : RUNNING

Current Time:
2026-09-22 21:20

YouTube     : BLOCKED (block · always blocked)
Shorts      : BLOCKED (auto · currently in the blocking window)
Roblox      : BLOCKED (auto · currently in the blocking window)

Schedule:
Mon-Thu 21:00 ~ 07:00
Fri     23:00 ~ 08:00
Sat     OFF
Sun     21:00 ~ 07:00

Temporary Permit:
None

DNS:
DNS Mode      : ProxyWithHostsFallback
DNS Proxy     : RUNNING
Fallback      : NOT ACTIVE
Upstream DNS  : 1.1.1.1, 8.8.8.8

Browser Policy:
chrome, edge (incognito blocked / guest blocked / DoH off)
```

If the DNS proxy couldn't start, this part looks like this instead:

```
DNS:
DNS Mode      : ProxyWithHostsFallback
DNS Proxy     : FAILED
Fallback      : HOSTS ACTIVE
Last Error    : Cannot bind 127.0.0.1:53 (AddressAlreadyInUse)
```

### Running from the console during development

```powershell
# Point it at a separate data folder so real settings aren't touched.
$env:TIMEBLOCKER_DATA = "D:\temp\tbdata"
.\src\TimeBlocker.Service\bin\Release\net8.0-windows\TimeBlocker.Service.exe
```

---

## 8. Telegram commands

The leading slash (`/`) is optional. Commands are case-insensitive.

### Status

| Command | Alias | Description |
|---|---|---|
| `status` | `s` | Full status |
| `targets` | | Per-target state (auto / block / unblock) |
| `schedule` | `sch` | Weekly blocking hours |
| `domains youtube` | | List of blocked domains |
| `maxpermit` | | Maximum permit length |
| `admin list` | | Registered admin User IDs |
| `ping` | | Liveness check + uptime |
| `version` | | Version / runtime / OS |
| `help` | `h` | Command list |

### Temporary permits

```
youtube 30      (yt 30)     Allow YouTube for 30 minutes starting now
roblox 60       (rb 60)     Allow Roblox for 60 minutes
all 20                      Allow everything for 20 minutes
```

Response:

```
OK
YouTube allowed for 30 minutes.

Start  : 21:15
Expire : 21:45
```

When the allowed time runs out, blocking **resumes automatically with no further action needed.**

### Changing the schedule

```
schedule mon 21:00 07:00
schedule mon-thu 21:00 07:00
schedule fri-mon 23:00 08:00     day ranges can wrap across the week
schedule weekday 21:00 07:00     Mon-Fri
schedule weekend 22:00 09:00     Sat, Sun
schedule sat off                 no restriction that day
schedule default 21:00 07:00     default for every day
```

Times accept either `21:00` or the compact `2100` format.

### Target state — block / unblock / auto

Each target is always in **exactly one of three states**.

| State | Command | Meaning |
|---|---|---|
| Schedule (default) | `auto youtube` | Blocked only during scheduled hours |
| Locked | `block youtube` | Always blocked, regardless of the schedule |
| Open | `unblock youtube` | Always open, regardless of the schedule |

The target is `youtube` / `roblox` / `shorts` / `all`. Omitting the target applies to everything
(`block` = `block all`).

`shorts` blocks **only Shorts**. Regular YouTube videos still play. It uses a completely
different blocking mechanism from the other targets and needs separate setup — see
"Blocking Shorts only".

Checking with `targets` looks like this:

```
YouTube : block   Locked (always blocked)
Shorts  : auto    Automatic (follows the schedule)
Roblox  : auto    Automatic (follows the schedule)
```

`status` shows the result and the reason together:

```
YouTube     : BLOCKED (block · always blocked)
Shorts      : BLOCKED (auto · currently in the blocking window)
Roblox      : ALLOW   (auto · not currently in the blocking window)
```

**Changing the state also cancels that target's temporary permit.** A temporary permit is an
exception that overrides the schedule, so if it's left in place the command you just issued
would appear to have no effect.

> **`block` locks it immediately; `auto` hands it back to the schedule.**
> "I set the schedule, so why isn't it blocked?" is almost always because that target is in
> `unblock` state. In that case, `status` also prints an `auto <target>` hint.

#### Relationship with temporary permits

A temporary permit like `youtube 30` is, independent of the state, **"open just for now."** It
still works even while a target is `block`-locked, and reverts to the underlying state once it
expires.

Decision priority:

1. `unblock` → always open
2. An active temporary permit → open
3. `block` → always blocked
4. Inside the blocking schedule → blocked
5. Otherwise → open

#### Backward compatibility with older commands

Commands used before v1.1 still work.

| Old command | Current meaning |
|---|---|
| `lock youtube` | `block youtube` |
| `enable youtube` | `auto youtube` |
| `disable youtube` | `unblock youtube` |

The `Enabled` field in the config file is also automatically converted to `Mode` the first time
it's read, so overwriting an existing install keeps its settings.

### Sending a message to the PC

```
msg have dinner first
say that's enough for tonight
```

This pops up a window on the child's PC. If the tray app is running, **you can receive a
reply**, which is delivered to the registered admin's Telegram.

If no one is logged in on the PC, it reports failure rather than claiming it was sent.

### Managing domains

```
domains youtube
domain add youtube music.youtube.com
domain remove youtube music.youtube.com
```

Malformed input — `http://...`, embedded spaces, an IP like `1.2.3.4` — is rejected.

### Configuration

```
maxpermit          show the current maximum permit length
maxpermit 90       change it (1 to 720 minutes)
reload             reload the config file + recompute the policy
```

### DNS diagnostics / recovery

```
dns status         current DNS operating state (same as the DNS section of `status`)
dns test           run a self-test right now
dns restore        immediately restore the adapter's DNS to its saved original setting
```

Example `dns status` response:

```
DNS Mode           : ProxyWithHostsFallback
DNS Proxy          : RUNNING
Adapter DNS        : 127.0.0.1
Auto Configure     : ENABLED
DNS Self Test      : OK
Original DNS Saved : YES
Fallback           : NOT ACTIVE
Adapters           : Wi-Fi
Upstream DNS       : 1.1.1.1, 8.8.8.8
```

During a failure:

```
DNS Mode           : ProxyWithHostsFallback
DNS Proxy          : FAILED
Adapter DNS        : (original setting)
Auto Configure     : ENABLED
DNS Self Test      : FAILED (proxy is not responding)
Original DNS Saved : NO
Fallback           : HOSTS ACTIVE
Last Error         : Adapter DNS change failed: ...
```

Example `dns test` response:

```
OK

Proxy direct     : OK (OK)
  allowed (example.com) : resolved
  blocked (googlevideo.com) : blocked
System resolver  : blocked OK
  allowed resolve: OK
Adapter DNS      : Wi-Fi=127.0.0.1
```

**`dns restore` is an immediate fix when the internet seems broken.** It restores the adapter's
DNS to its original setting while keeping blocking active via the hosts method. Run `reload` to
switch back to proxy mode.

### Automatic diagnostics (doctor)

A comprehensive check for quickly finding the cause when something's wrong.

```
doctor
```

Can also be run locally, and **works even if the service is stopped**, since every check is done
by observing the system from the outside.

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" doctor
```

Checks include: administrator rights · service installed/running · port 53 availability ·
DNS proxy IPv4/IPv6 listeners · active adapters · adapter DNS · original-DNS backup · whether
adapters point at the proxy · normal domain resolution · blocked domain actually blocked ·
hosts fallback state · hosts managed-region integrity · firewall rules · Roblox executable ·
Telegram config/admin ID/API connectivity · config file validation · state file read/write ·
data directory ACL · log directory writable · notifier tray app connection

Sample output:

```
TimeBlocker Doctor

[PASS] Administrator : SYSTEM
[PASS] Service installed : TimeBlocker
[PASS] Service running : Running
[PASS] Port 53 (UDP) : in use by TimeBlocker
[PASS] DNS Proxy IPv4 : 127.0.0.1:53
[PASS] DNS Proxy IPv6 : [::1]:53
[PASS] Active adapter : Wi-Fi
[PASS] Adapter DNS : 127.0.0.1
[PASS] Original DNS backup : Wi-Fi
[PASS] Adapter -> proxy : connected to 127.0.0.1
[PASS] Normal DNS resolution : example.com -> 93.184.216.34
[PASS] Blocked domain : googlevideo.com blocked
[PASS] Hosts fallback : INACTIVE (proxy in use)
[PASS] Firewall rules : TimeBlocker_Roblox_Block
[WARN] Roblox executable : not installed
[PASS] Telegram API : @MyTimeBlockerBot
[PASS] Data directory ACL : writable only by SYSTEM/Administrators

Result : PASS (1 warning)
```

If there's a problem, a suggested fix is printed alongside each item.

```
Suggested fixes:
- Service running: run as administrator: Start-Service TimeBlocker
- Adapter -> proxy: run immediately: TimeBlocker.Service.exe dns-restore
```

Exit code: `0` if everything PASSes or WARNs, `2` if any item FAILs.

If the service is stopped entirely and Telegram / CLI don't work, run this directly on the PC:

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
```

### Invalid commands

No exceptions are thrown — usage help is returned instead.

```
> youtube abc

ERROR
Invalid minutes.

Usage:
youtube <minutes>

Example:
youtube 30
```

---

## 9. Local admin CLI

A helper tool in place of a GUI. **Its command syntax is identical to Telegram's.**

```powershell
# Must be run from a terminal that was itself launched "as administrator"
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe youtube 30
TimeBlocker.Admin.exe schedule mon 21:00 07:00
TimeBlocker.Admin.exe block youtube
TimeBlocker.Admin.exe logs 200
```

- The CLI does not run the core logic itself. It **only forwards commands to the service over a
  Named Pipe.**
- Actual access changes are always performed by the Windows Service.
- The Named Pipe's DACL is open only to SYSTEM / Administrators, so a regular user account
  **cannot even view status.**

---

## 10. How blocking works

### YouTube — DNS-based

The mode is chosen by the config's `Dns.Mode`.

| Mode | Behavior | Subdomain blocking |
|---|---|---|
| `ProxyWithHostsFallback` **(default)** | Prefers the 127.0.0.1 DNS proxy, and automatically falls back to hosts if it fails to start or hits a fatal error. | Yes while the proxy runs / No during fallback |
| `Proxy` | Proxy only. If the proxy can't start, domain blocking isn't applied. | Yes |
| `Hosts` | Only adds/removes the TimeBlocker-managed region of the hosts file. | No |

### Why `ProxyWithHostsFallback` is the default

YouTube uses a large number of **dynamic subdomains**, like `rr1---sn-ab5l6nz7.googlevideo.com`.
The hosts file doesn't support wildcards and can't block these, and they change too often to
list in advance. The DNS proxy approach is the right fit for real use, and it only falls back to
hosts in environments where the proxy can't be started at all.

> If you already have it installed and the config file says `"Mode": "Hosts"`, **that setting is
> kept as-is.** Only the default for *new* installs changed — existing settings are never
> overwritten. To switch to proxy mode, change `Mode` to `ProxyWithHostsFallback` in the config
> file and run `TimeBlocker.Admin.exe reload`.

### Operating priority

```
DNS Proxy healthy
    ↓
Use proxy-based blocking  (subdomains included)

DNS Proxy fails to initialize or hits a fatal error
    ↓
Fall back to hosts  (only exact-match domains blocked)
```

The limitation is logged clearly when it falls back.

```
WARN  DNS Proxy unavailable. Hosts fallback activated.
      Wildcard/subdomain blocking capability is limited.
      Cause: Cannot bind 127.0.0.1:53 (AddressAlreadyInUse)
```

While the proxy is running, **dynamic domains are never bulk-written into hosts.** All decisions
are handled by the proxy in memory. Even during fallback, the proxy retries starting every
minute, and switches back to proxy mode automatically once it succeeds.

```
INFO  DNS proxy started: 127.0.0.1:53 (upstream: 1.1.1.1, 8.8.8.8)
INFO  DNS proxy recovered. Releasing hosts fallback.
```

### Safe procedure for changing adapter DNS

For the proxy approach to actually work, the network adapter's DNS must point at 127.0.0.1
(`Dns.AutoConfigureAdapters = true`). Getting this wrong **can cut off the PC's internet
entirely**, so the following sequence is followed strictly.

```
1. Start DNS Proxy
       ↓
2. Query 127.0.0.1 directly for a self-test
     - Does an allowed domain (example.com) resolve?
     - Is a blocked domain rejected with our NXDOMAIN?
       ↓  On failure, stop here (adapters are never touched)
3. Save the original DNS settings to disk  ← on save failure, also stop
       ↓
4. Change the adapter's DNS to 127.0.0.1 (+ ::1)
       ↓
5. Flush the DNS cache and re-verify through the system resolver
     - Is a blocked domain actually blocked?
     - Does an allowed domain actually resolve?
       ↓  On failure, roll back to the original settings immediately
6. Done
```

**If any step fails, the adapter is left in its original state.** Blocking continues via hosts
fallback.

#### Saving the original setting

Before making any change, this is saved to `%ProgramData%\TimeBlocker\state\adapter-dns.json`.

```jsonc
{
  "Adapters": [
    {
      "Name": "Wi-Fi",
      "Ipv4Dhcp": true,          // whether it used DHCP-assigned DNS
      "Ipv4Servers": [],         // the manual DNS list, if it had one
      "Ipv6Dhcp": true,
      "Ipv6Servers": [],
      "Ipv4Changed": true,       // the fields we actually changed
      "Ipv6Changed": true
    }
  ],
  "ProcessId": 1234,
  "AppliedAtUtc": "2026-09-22T12:00:00+00:00"
}
```

- The original setting is **read directly from the registry.** (Parsing `netsh` output depends
  on Windows' display language and can break on a Korean-language Windows install.)
- This file survives a PC reboot.
- **It is deleted once restoration completes normally.** So if it's still present at startup, the
  previous run is treated as having exited uncleanly.
- It's human-readable, so in the worst case you can use it to restore the settings by hand.

#### Recovering from an unclean shutdown

This is the very first thing the service does at startup — restoring a safe state **before**
initializing the proxy.

```
Service starts
   ↓
Does adapter-dns.json still exist?
   ├─ NO  → previous run exited cleanly. Continue
   └─ YES → the previous run exited uncleanly
              ↓
            restore the saved original DNS settings
              ↓
            flush the DNS cache
              ↓
            then initialize the DNS Proxy
```

If any adapter fails to restore, the **state file is kept**, not deleted, so it's retried on the
next startup.

#### Continuous monitoring (fail-safe)

While the proxy runs, a self-test runs every 2 minutes.

```
Health check fails (2 in a row)
   ↓
Restore the adapter's DNS to its original setting   ← restore internet first
   ↓
Activate hosts fallback                              ← keep blocking active
   ↓
Log it
```

The internet is never left broken.

> Note: a failure to reach the upstream DNS (i.e. the internet itself is down) is distinguished
> from a proxy failure. In that case the adapter is not rolled back, and only a warning is
> logged, since rolling back wouldn't restore the internet anyway.

#### Behavior on a clean shutdown (resolving the conflict with staying blocked)

Two requirements conflict when the service is stopped.

| Requirement | Required behavior |
|---|---|
| The internet must not break while the service is down | The adapter's DNS must be restored |
| Blocking must not be defeatable simply by stopping the service | Blocking must remain active |

TimeBlocker satisfies both.

```
Clean shutdown
   ↓
1. Restore the adapter's DNS to its original setting   (guarantees internet)
2. Stop the DNS Proxy
3. Write the currently-blocked domains into hosts        (keeps blocking active)
4. Firewall rules are left as they are
```

In other words, **while the service is stopped, blocking continues via the hosts method.**
Since hosts doesn't support wildcards, subdomain blocking is limited during this time. Once the
service starts again, it returns to proxy mode.

#### The IPv6 DNS path

If only IPv4 DNS is switched to 127.0.0.1, Windows may query an IPv6 DNS server and **bypass the
proxy**. So the proxy listens on both `127.0.0.1` and `[::1]`, and both the adapter's IPv4 and
IPv6 DNS are changed together.

- This can be turned off with `Dns.ConfigureIpv6` (default `true`).
- If the IPv6 listener fails to open, **the adapter's IPv6 DNS is not changed.**
  (Pointing it at `::1` with nothing listening there would break IPv6 name resolution
  completely.) In that case, a warning about possible bypass is logged.

#### Which adapters are chosen

No adapter is touched arbitrarily. Only ones meeting **all** of these conditions are changed:

- Connected (`Up`)
- Wired (Ethernet) or wireless (Wireless80211)
- Loopback / tunnel excluded
- Virtual adapters excluded (VMware, Hyper-V, VirtualBox, WSL, Npcap, VPN, TAP, Bluetooth, etc.)
- Adapters with a gateway are preferred (ones actually connected to the internet)

Each changed adapter and its original setting are saved individually.

The hosts approach manages **only** the marker region, **without damaging existing content.**

```
# TIMEBLOCKER BEGIN
# This region is managed automatically by TimeBlocker. Do not edit it directly.
0.0.0.0 youtube.com
::1 youtube.com
# TIMEBLOCKER END
```

How the DNS proxy behaves:

```
Application
   ↓
127.0.0.1 Local DNS Proxy
   ↓
Is it a blocked target?
 ├─ YES → NXDOMAIN
 └─ NO  → forward to upstream DNS (1.1.1.1 / 8.8.8.8)
```

### Subdomain-matching rule

The proxy checks not just for an exact match but also subdomains. There is exactly one rule:

```
domain == blockedDomain
OR
domain.EndsWith("." + blockedDomain)
```

A substring search (`Contains`) is never used, because that would block unrelated domains too.

With only `googlevideo.com` in the config, here's how queries are judged:

| Queried domain | Result | Reason |
|---|---|---|
| `googlevideo.com` | **BLOCK** | Exact match |
| `rr1---sn-xxxx.googlevideo.com` | **BLOCK** | Subdomain |
| `rr2---sn-xxxx.googlevideo.com` | **BLOCK** | Subdomain |
| `abc.googlevideo.com` | **BLOCK** | Subdomain |
| `notgooglevideo.com` | ALLOW | No leading dot — just a substring |
| `googlevideo.com.evil.com` | ALLOW | Not a suffix |

The default config includes the following families, each with subdomains also blocked:

```
youtube.com
youtu.be
googlevideo.com
ytimg.com
youtubei.googleapis.com
```

So `music.youtube.com` and `www.youtube.com` are both blocked by `youtube.com` alone, while
`myyoutube.com` is not blocked.

The domain list is not hardcoded in the source — it's managed via the config file / Telegram
commands.

### Roblox — DNS + firewall

- DNS blocking (same as above)
- Windows Firewall outbound blocking
  - Rule name: `TimeBlocker_Roblox_Block`
  - Auto-discovers the executable at: `C:\Users\*\AppData\Local\Roblox\Versions\*`, Bloxstrap,
    `Program Files\Roblox`
  - When unblocked, the rule is **deleted**, and automatically re-created the next time the
    blocking window starts.
  - Re-scans every time, so it keeps up even if Roblox is updated and its path changes.
  - **If the executable can't be found, the rule is never created.** In that case a warning is
    logged and only DNS blocking applies. If it's installed somewhere else — e.g. the Microsoft
    Store edition — add the full path under `ExtraExecutablePaths` in the config.
- Process termination (see below)

### Terminating a running game

DNS and firewall alone sometimes can't stop **a game that's already running.** Once a Roblox
client connects, it talks directly to the game server's IP, so domain blocking has no effect,
and a firewall rule is never created if the executable couldn't be found.

So if the target process is running during a blocking window, it's given a grace period and then
terminated.

```
0 min   On-screen warning: "Will automatically close at 21:05, in 5 minutes."
4 min   "Closing in 1 minute. Save your work now."
5 min   Terminated + "Closed because it's currently blocking hours."
```

- Granting a permit (`roblox 30`) or `unblock`-ing during the grace period **cancels the
  scheduled termination.** If it's blocked again later, the grace period starts over from
  scratch.
- If the child closes it themselves, the scheduled termination is also cancelled.
- Only **Roblox** is enabled by default. YouTube would require closing the whole browser, so it's
  off by default. (`TerminateProcesses`)
- The grace period is adjustable via `Enforcement.TerminationGraceMinutes`. `0` terminates
  immediately.

Only official Windows APIs are used. Nothing hides the process or bypasses any security feature.

### On-screen notifications and replies

The service runs in Session 0, so it can't show a window on the screen. A small tray app that
runs in the user's own session (`TimeBlocker.Notifier.exe`) is installed alongside it for that.

| | With the tray app running | Without it |
|---|---|---|
| Warning display | Tray app's own window | Windows' built-in message box |
| Replying | Possible | Not possible |

- It launches automatically at logon (`TimeBlockerNotifier` under `HKLM\...\Run`). It isn't
  hidden — it shows up in Task Manager's Startup tab like any other program.
- If the child closes this app, **blocking itself still works normally.** Only the warning
  changes to Windows' built-in message box.
- When the admin sends `msg <text>`, it appears on the PC's screen. The child can reply from
  that window, and the reply is delivered to the registered admin's Telegram.

**Security:** the notification channel is a **separate pipe**, distinct from the management
command pipe. Since a child's account (a standard user) needs to be able to connect to it, this
channel can only do two things: "receive notifications" and "send a reply." There is no way to
change settings or unblock anything through it. To prevent spam, replies are rate-limited to a
10-second cooldown and 20 per hour.

### Blocking Shorts only — browser policy

**Shorts cannot be blocked with DNS.** Shorts and regular videos use the same `youtube.com`,
the same `googlevideo.com`, and even the same TLS connection. DNS blocking works at the domain
level, so blocking `youtube.com` kills all of YouTube, and not blocking it leaves Shorts open.
The only mechanism that can see the path (`/shorts/...`) is **browser policy**.

So the `shorts` target takes a different route.

| Target | Blocking mechanism |
|---|---|
| YouTube / Roblox | DNS (domains) + firewall + process termination |
| Shorts | Browser policy (`URLBlocklist` in the HKLM registry) |

#### Turning it on

Set `BrowserPolicy.Enabled` to `true` in the configuration file and restart the service.
**It is off by default** — it touches registry policy, so it must be enabled explicitly.

```jsonc
"BrowserPolicy": {
  "Enabled": true,
  "Browsers": [ "chrome", "edge" ]
}
```

Then set the state over Telegram:

```
block shorts      always block Shorts
auto shorts       block Shorts only during scheduled hours
unblock shorts    don't block Shorts
shorts 30         open Shorts for 30 minutes
```

The `Browser Policy` line in `status` and the `Browser policy` item in `doctor` tell you whether
it actually applied. You can also open `chrome://policy` in the browser to see `URLBlocklist`
directly.

#### What gets blocked and what doesn't

Two patterns go in by default:

```
youtube.com/shorts            the Shorts watch page
youtube.com/youtubei/v1/reel/ the internal API the Shorts feed uses to fetch the next video
```

A host entry covers its subdomains, so `youtube.com/shorts` alone catches both
`www.youtube.com/shorts/<id>` and `m.youtube.com/shorts/<id>`.

| Situation | Result |
|---|---|
| Typing a Shorts URL in the address bar | Blocked |
| Opening a Shorts link in a new tab / reloading | Blocked |
| Regular YouTube videos (`/watch`) | **Still work** (intended) |
| Tapping a Shorts thumbnail from the YouTube home page | **Not blocked if no page load happens** |
| The Shorts shelf on the home page | Still visible |

Those last two rows are the limitation of this approach. YouTube navigates by rewriting the page
in JavaScript (SPA), and `URLBlocklist` only stops **real page loads**, so it never sees that
navigation. Google's and Microsoft's own documentation states this limitation. We block the
internal API the Shorts feed uses to compensate, so videos stop advancing — but it is not
complete, and **how much leaks through has to be checked on your child's PC.**

Blocking it completely requires a force-installed extension, which is outside the scope of
this feature.

#### Bypass routes are closed too

Turning on `URLBlocklist` alone leaves several ways out. The same settings close them.

| Setting | Default | What it closes |
|---|---|---|
| `DisableIncognito` | `true` | Incognito mode (`IncognitoModeAvailability=1`) |
| `DisableGuestMode` | `true` | Guest mode (a separate profile, so policies/extensions are weaker) |
| `DisableDnsOverHttps` | `true` | Browser DoH — **if left on, it bypasses YouTube/Roblox DNS blocking too** |
| `BlockExtensionInstalls` | `false` | Installing VPN/proxy extensions (`ExtensionInstallBlocklist=["*"]`) |

**These stay applied the whole time the feature is on, regardless of the blocking window.**
If incognito were only blocked during blocking hours, a child could simply open an incognito
window beforehand.

`BlockExtensionInstalls` can also stop extensions already in use, so it is off by default.
When enabling it, list the extension IDs to keep in `ExtensionAllowlist`.

#### Other browsers

**Policies are per-browser.** Setting them for Chrome/Edge has no effect if the child switches
to another browser.

| Browser | Policy key | Status |
|---|---|---|
| Chrome | `SOFTWARE\Policies\Google\Chrome` | Confirmed in official docs |
| Edge | `SOFTWARE\Policies\Microsoft\Edge` | Confirmed in official docs |
| Brave | `SOFTWARE\Policies\BraveSoftware\Brave-Browser` | **Assumed — needs verification** |
| Whale (Naver) | `SOFTWARE\Policies\Naver\Whale` | **Assumed — needs verification** |
| Opera | `SOFTWARE\Policies\Opera Software\Opera` | **Assumed — needs verification** |

Only the confirmed `chrome` / `edge` are defaults. Putting assumed paths in the defaults would
silently produce a "thought it was blocked but wasn't" state.

To use a browser with an assumed path, **verify it first**:

1. Add it to `Browsers` and restart the service
2. Open the browser's policy page (e.g. `whale://policy`) and check that `URLBlocklist` appears
3. If it doesn't, find the real path under `HKLM\SOFTWARE\Policies` with `regedit` and set it
   via `RegistryKeyOverrides`

```jsonc
"BrowserPolicy": {
  "Enabled": true,
  "Browsers": [ "chrome", "edge", "whale" ],
  "RegistryKeyOverrides": {
    "whale": "SOFTWARE\\Policies\\Naver\\Whale"
  }
}
```

Firefox is not Chromium-based, so this approach does not work there at all (it uses a separate
`policies.json` mechanism). Not installing browsers you don't intend to manage is the surest
option.

#### How your existing settings are protected

Because this touches the registry, it follows exactly the same safety procedure as adapter DNS.

```
1. Save original values to state\browser-policy.json before writing
   (if the save fails, the registry is left alone — never leave a change you can't undo)
2. Never re-read the backup for a browser that already has one
   (overwriting the "original" with our own values makes recovery impossible forever)
3. Keep URLBlocklist entries the parent already had; only add our own
4. When blocking lifts, remove only our entries; delete keys that didn't exist before
```

- Setting `BrowserPolicy.Enabled` back to `false` **restores everything on the next evaluation cycle.**
- `cleanup` and uninstall restore it too (the `Browser policy restore` step).
- With no backup present, the registry is **left untouched.** Guessing would destroy someone else's settings.
- If someone deletes the policy from the registry, it is written back on the next cycle.
  (Nothing is written when the contents already match, so the steady-state cost is negligible.)

#### Can the child delete the policy?

**No.** Writing to `HKLM` requires administrator rights, so a child on a **standard user**
account cannot edit the registry. (If that assumption breaks, all of TimeBlocker is pointless —
see section 5.)

The browser's extensions/policy page does **show** it as "Installed by your administrator".
Being visible and being removable are different things.

---

### DNS cache

The cache is flushed **only when the block/allow state actually changes.** It calls
`dnsapi.dll`'s `DnsFlushResolverCache` directly, falling back to `ipconfig /flushdns` on failure.

---

## 11. Configuration file

Location: `%ProgramData%\TimeBlocker\timeblocker.config.json`
(can be relocated via the `TIMEBLOCKER_DATA` environment variable — for development/testing)

```jsonc
{
  "ConfigVersion": 1,
  "Schedule": {
    "Days": [
      { "Day": "Monday",   "Enabled": true,  "Start": "21:00", "End": "07:00" },
      { "Day": "Friday",   "Enabled": true,  "Start": "23:00", "End": "08:00" },
      { "Day": "Saturday", "Enabled": false, "Start": "21:00", "End": "07:00" }
    ]
  },
  "Dns": {
    "Enabled": true,
    "Mode": "ProxyWithHostsFallback",   // Hosts / Proxy / ProxyWithHostsFallback
    "UpstreamServers": [ "1.1.1.1", "8.8.8.8" ],
    "ProxyPort": 53,
    "AutoConfigureAdapters": true,     // automatically point the adapter's DNS at 127.0.0.1
    "ConfigureIpv6": true,             // also change IPv6 DNS (::1)
    "SelfTestDomain": "example.com",   // domain used in the self-test as "should resolve"
    "FlushCacheOnChange": true
  },
  "YouTube": {
    "Mode": "Schedule",                // Schedule / Blocked / Open
    "UseDnsBlocking": true,
    "UseFirewallBlocking": false,
    "Domains": [ "youtube.com", "www.youtube.com", "youtu.be", "googlevideo.com", "ytimg.com" ]
  },
  "Roblox": {
    "Mode": "Schedule",
    "UseDnsBlocking": true,
    "UseFirewallBlocking": true,
    "TerminateProcesses": true,
    "Domains": [ "roblox.com", "rbxcdn.com" ],
    "ProcessNames": [ "RobloxPlayerBeta.exe", "RobloxStudioBeta.exe" ]
  },
  "Shorts": {
    // Shorts only. DNS can't tell paths apart, so this uses browser policy exclusively.
    "Mode": "Schedule",                   // block shorts / auto shorts / unblock shorts
    "UseDnsBlocking": false,              // turning this on would block all of youtube.com. Don't
    "UseFirewallBlocking": false,
    "UseBrowserPolicyBlocking": true,
    "BlockedUrlPatterns": [
      "youtube.com/shorts",               // subdomains (www / m) are covered too
      "youtube.com/youtubei/v1/reel/"     // internal API the Shorts feed uses for the next video
    ]
  },
  "BrowserPolicy": {
    // Off by default. It touches registry policy, so it must be enabled explicitly.
    "Enabled": false,
    "Browsers": [ "chrome", "edge" ],     // only confirmed paths are defaults
    "RegistryKeyOverrides": {},           // fix a wrong built-in path here
    "DisableIncognito": true,             // closes the incognito escape hatch
    "DisableGuestMode": true,
    "DisableDnsOverHttps": true,          // if left on, all DNS blocking is bypassed
    "BlockExtensionInstalls": false,      // blocks VPN/proxy extensions. Off by default (side effects)
    "ExtensionAllowlist": []
  },
  "TemporaryPermit": { "MaxMinutes": 120, "MinMinutes": 1 },
  "Telegram": {
    "Enabled": true,
    "ProtectedBotToken": "(value encrypted with DPAPI)",
    "AllowedUserIds": [ 123456789 ],
    "PollTimeoutSeconds": 30
  },
  "Logging": { "MinimumLevel": "INFO", "MaxFileSizeMb": 8, "RetainDays": 30 },
  "Enforcement": { "EvaluationIntervalSeconds": 10, "TimeJumpThresholdMinutes": 5, "TerminationGraceMinutes": 5 }
}
```

- Saving **writes to a temp file and swaps it in atomically**, so a power loss mid-write never
  corrupts the existing file.
- If the file becomes corrupted, it's backed up as `.broken` and the service **keeps running on
  default settings.** The service never dies over this.
- The data folder has an ACL applying write access only to SYSTEM / Administrators.

---

## 12. Logging

Location: `%ProgramData%\TimeBlocker\logs\TimeBlocker-YYYY-MM-DD.log`

- Levels: `TRACE` / `DEBUG` / `INFO` / `WARN` / `ERROR`
- One file per day + size-based rolling (`TimeBlocker-2026-09-22.1.log`) + files older than
  `RetainDays` are deleted automatically
- Every remote command is recorded as an audit log entry.

```
2026-09-22 21:00:00 [INFO ] BlockingCoordinator: YouTube BLOCK (InBlockingSchedule)
2026-09-22 21:00:00 [INFO ] BlockingCoordinator: Roblox BLOCK (InBlockingSchedule)
2026-09-22 21:15:02 [INFO ] RemoteCommandHandler: REMOTE COMMAND | Telegram:123456789 | Command=youtube 30 | Result=SUCCESS
2026-09-22 21:15:02 [INFO ] TemporaryPermitManager: Temporary permit: YouTube 30 min (requested by: Telegram:123456789, expires: 2026-09-22 21:45)
2026-09-22 21:20:11 [INFO ] RemoteCommandHandler: REMOTE COMMAND | Telegram:123456789 | Command=schedule mon 22:00 07:00 | Result=SUCCESS
2026-09-22 21:45:03 [INFO ] TemporaryPermitManager: Temporary permit expired: YouTube
2026-09-22 21:45:03 [INFO ] BlockingCoordinator: YouTube BLOCK (InBlockingSchedule)
```

The Bot Token is never written to a log, under any circumstance.

To view:

```powershell
TimeBlocker.Admin.exe logs 200
```

---

## 13. Testing

### Unit tests

```powershell
dotnet test tests\TimeBlocker.Tests\TimeBlocker.Tests.csproj
```

What's covered:

- Plain time-range decisions (`09:00~17:00`)
- **Ranges crossing midnight** (`21:00~07:00` → 20:59 ALLOW / 21:00 BLOCK / 00:30 BLOCK / 06:59
  BLOCK / 07:00 ALLOW)
- Per-day settings, including a Friday-night rule that runs into Saturday morning
- Granting a temporary permit / expiry boundaries (30 minutes granted at 21:10 → 21:39 ALLOW,
  21:40 BLOCK)
- **Reboot recovery** (60 minutes granted at 21:00 → service restarted at 21:20 → blocked exactly
  at 22:00)
- Rejecting a request over the max permit length; re-granting the same target replaces rather
  than accumulates
- Policy engine priority (Open > Permit > Blocked > Schedule)
- The full Telegram command parser (aliases, day ranges, domain format validation, bad input)
- Command handler scenarios (save config → recompute policy → respond)
- hosts file manipulation (existing content preserved, only the marker region replaced,
  corruption recovery)
- DNS message parsing / NXDOMAIN generation / subdomain matching
- The termination grace-period state machine (warn → final warning → terminate → cancel on
  permit/unblock → restart the grace period on re-block)
- The reply rate limiter (cooldown, hourly cap, truncation, newline stripping)

### Real Telegram integration test procedure

1. Finish installing and configuring Telegram (sections 4 and 6).
2. Confirm the service is Running.
   ```powershell
   sc.exe query TimeBlocker
   ```
3. Open the chat with the bot on Telegram and send these **in order**.

| # | Message | Expected result |
|---|---|---|
| 1 | `status` | `Service : RUNNING`, current time, YouTube/Roblox status, schedule, `Temporary Permit: None` |
| 2 | `schedule mon-thu 21:00 07:00` | `OK` + Monday–Thursday now listed as `21:00-07:00` |
| 3 | `youtube 30` | `OK` + `YouTube allowed for 30 minutes.` + Start / Expire time |
| 4 | `status` | `YouTube : ALLOW`, `Temporary Permit:` shows `YouTube until HH:mm (30 min left)` |
| 5 | `block youtube` | `OK` + a note that the temporary permit was cancelled + current status |
| 6 | `status` | `Temporary Permit: None`. If it's within the blocking window, `YouTube : BLOCKED` |

> To visually see the state changes in steps 3–6, test **inside a blocking window.**
> To test right away, switch to blocking all day with `schedule default 00:00 23:59`, test, then
> revert it afterward.

4. Verifying blocking for real
   - Visit `https://www.youtube.com` in a browser → should fail to connect
   - Send `youtube 5` and revisit → should now connect
   - After 5 minutes, blocking resumes automatically
   - If it doesn't reflect immediately due to the browser's cache, open a new tab or restart the
     browser.

5. Verifying reboot recovery
   - Send `youtube 60` and note the expiry time.
   - Reboot the PC.
   - After boot, `status` → the same expiry time should still be there.
   - Once the expiry time passes, it should be blocked again with no further action.

6. Verifying authorization (from a different Telegram account)
   - Send `status` to the bot from an unregistered account.
   - There should be no response, and the log should show:
     ```
     Unauthorized Telegram access attempt UserId=xxxx
     ```

### Verifying the adapter DNS safety procedure (requires administrator rights)

With `Dns.AutoConfigureAdapters = true` on a real install, run through the following scenarios in
order, in an **Administrator PowerShell.**

Check the actual adapter DNS before and after each step.

```powershell
Get-DnsClientServerAddress -AddressFamily IPv4 |
    Where-Object { $_.ServerAddresses.Count -gt 0 } |
    Format-Table InterfaceAlias, ServerAddresses
```

#### Scenario 1. Proxy starts successfully → DNS changes

```powershell
Start-Service TimeBlocker
Start-Sleep -Seconds 10
TimeBlocker.Admin.exe dns status
```

Expected:

```
DNS Proxy          : RUNNING
Adapter DNS        : 127.0.0.1
DNS Self Test      : OK
Original DNS Saved : YES
Fallback           : NOT ACTIVE
```

- Confirm the real adapter is `127.0.0.1` via `Get-DnsClientServerAddress`
- Confirm `%ProgramData%\TimeBlocker\state\adapter-dns.json` was created and **contains the
  original DNS**
- Confirm the internet works normally (visit any site in a browser)

#### Scenario 2. Proxy fails to start → DNS is not changed

Start the service while port 53 is already occupied.

```powershell
Stop-Service TimeBlocker
# In a separate window, hold port 53
$ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Loopback, 53)
$holder = New-Object System.Net.Sockets.UdpClient($ep)

Start-Service TimeBlocker
Start-Sleep -Seconds 10
TimeBlocker.Admin.exe dns status
```

Expected:

```
DNS Proxy          : FAILED
Adapter DNS        : (original setting)
Original DNS Saved : NO
Fallback           : HOSTS ACTIVE
```

- **The adapter's DNS should not have changed at all.** (Internet normal)
- The log should contain `DNS Proxy unavailable. Hosts fallback activated.`
- Once the port is freed, the proxy recovers within a minute and switches to `RUNNING`.

```powershell
$holder.Close()
Start-Sleep -Seconds 70
TimeBlocker.Admin.exe dns status
```

#### Scenario 3. DNS changed but self-test then fails → rollback

Point the upstream DNS at an unreachable address so the proxy stops responding correctly.

```powershell
TimeBlocker.Admin.exe dns test     # confirm normal before the change
# In the config file, change UpstreamServers to ["192.0.2.1"] (unreachable)
TimeBlocker.Admin.exe reload
Start-Sleep -Seconds 15
TimeBlocker.Admin.exe dns status
```

Expected: if the self-test fails, the adapter is either never changed, or is rolled back if it
already was, and it switches over to `Fallback : HOSTS ACTIVE`. **The internet must not be left
broken.**

Revert the config and `reload`.

#### Scenario 4. Clean service stop

```powershell
Stop-Service TimeBlocker
Get-DnsClientServerAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, ServerAddresses
Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String TIMEBLOCKER -Context 0,3
```

Expected:

- Adapter DNS is **restored to its original setting** → internet normal
- `adapter-dns.json` is **deleted**
- If it was inside a blocking window, the `# TIMEBLOCKER BEGIN` region remains in hosts, so
  **blocking continues**

#### Scenario 5. Forced termination and restart

```powershell
Start-Service TimeBlocker
Start-Sleep -Seconds 10
Stop-Process -Name TimeBlocker.Service -Force     # unclean shutdown
Get-DnsClientServerAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, ServerAddresses
```

At this point the adapter's DNS may be left at `127.0.0.1`, which can break internet access.
(This is intended.) The service's recovery setting auto-restarts it after 10 seconds, and
recovery happens on that restart.

```powershell
Start-Sleep -Seconds 20
Get-Service TimeBlocker        # should be back to Running
TimeBlocker.Admin.exe dns status
```

The log should contain:

```
WARN  The previous run did not exit cleanly. The original adapter DNS settings still exist, so they are restored first.
INFO  Restored adapter DNS to its original setting: Wi-Fi (...)
INFO  Unclean shutdown recovery complete
```

Manual recovery if auto-restart doesn't happen:

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
```

#### Scenario 6. PC reboot

```powershell
Restart-Computer
```

After boot:

- The service should auto-start (`Get-Service TimeBlocker`)
- The internet should be normal
- `TimeBlocker.Admin.exe dns status` shows `RUNNING` / `Adapter DNS : 127.0.0.1`
- If a temporary permit was still active before the reboot, its expiry time should be restored
  exactly as it was

If it was treated as an unclean shutdown during the reboot, the recovery log from Scenario 5
should appear.

#### Scenario 7. cleanup

```powershell
Stop-Service TimeBlocker
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" cleanup
```

Expected:

- The TIMEBLOCKER region in hosts is removed
- All `TimeBlocker_*` firewall rules are removed
- **Adapter DNS is restored to the original setting** (restored from the saved value, not
  unconditionally set to DHCP)
- The current adapter DNS is printed at the end so you can verify it visually

#### Scenario 8. Wi-Fi / Ethernet individually

Alternate between wired and wireless and check both.

```powershell
# With only Wi-Fi connected
TimeBlocker.Admin.exe dns status     # Adapters should show only Wi-Fi

# Connect wired and turn off Wi-Fi
TimeBlocker.Admin.exe reload
TimeBlocker.Admin.exe dns status     # Adapters should show Ethernet
```

Things to verify:

- A disconnected (`Down`) adapter is excluded.
- Virtual adapters (VMware, Hyper-V, VirtualBox, WSL, Npcap, VPN, etc.) are excluded.
- With the log level set to `DEBUG`, you can see `Adapter excluded: ...` explaining what was
  skipped and why.

### Integration smoke test (on a real install)

Run this in an **Administrator PowerShell** after installing.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```

> ★ This script is not read-only. It creates and cancels temporary permits, and restores then
> re-applies adapter DNS. It asks for confirmation before running.
> (`-Force` skips the confirmation.)

It checks 14 items in order.

| # | Item |
|---|---|
| 1 | Service running state |
| 2 | DNS Proxy state |
| 3 | Whether the active adapter's DNS is 127.0.0.1 |
| 4 | Normal internet DNS resolution |
| 5 | YouTube domain blocking |
| 6 | `googlevideo.com` subdomain blocking |
| 7 | A similarly-named domain (`notgooglevideo.com`) is not blocked |
| 8 | Creating a temporary permit |
| 9 | YouTube DNS opens up while the permit is active |
| 10 | Re-blocked after the permit is cancelled |
| 11 | `dns restore` works |
| 12 | DNS Proxy re-applies |
| 13 | Firewall rules |
| 14 | Telegram connectivity |

**Safety net:** if normal domain resolution fails partway through, the script stops immediately
and automatically restores the Original DNS. If the script is interrupted for any reason, its
`finally` block cancels any temporary permits and reverts the schedule and target states it
changed.

If the current time isn't inside a blocking window, it temporarily switches to blocking all day
for tests 5–10, and reverts to `21:00~07:00` afterward.

Result:

```
TimeBlocker Integration Test

 Tests : 14
 PASS  : 14
 FAIL  : 0

RESULT: READY
```

On failure, the failed items and a prompt to run `doctor` are printed together.
Exit codes: `0` READY, `1` NOT READY, `2` ABORTED (stopped due to an internet failure).

### Testing commands locally without Telegram

```powershell
# Administrator terminal
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe schedule mon-thu 21:00 07:00
TimeBlocker.Admin.exe youtube 30
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe block youtube
TimeBlocker.Admin.exe status
```

It uses exactly the same command processor as Telegram, so behavior is identical.

---

## 14. Known limitations

**Evading the block**

- **Subdomains can't be blocked during hosts fallback.** In the default mode
  (`ProxyWithHostsFallback`), when the proxy is healthy a single entry like `googlevideo.com`
  also blocks `rr1---sn-xxx.googlevideo.com`, but when the proxy fails to start and it falls back
  to hosts, only exact-match domains are blocked. You can see this in `status`'s
  `Fallback : HOSTS ACTIVE`, and it's also logged as a WARN. This usually happens when another
  DNS program is holding port 53, or when the service isn't running elevated.
- **DoH (DNS over HTTPS) bypasses DNS blocking.** You need to turn off the "Secure DNS" feature
  in Chrome / Edge / Firefox, or disable it via Group Policy.
- **A VPN, proxy, or mobile tethering** can be used to bypass this. That's outside this program's
  scope.
- **Shorts blocking (`shorts`) is not complete.** The browser policy `URLBlocklist` only stops
  real page loads. Navigation that only rewrites the URL in JavaScript (SPA) inside YouTube is
  not caught, so tapping a Shorts thumbnail from the home page can leak through. We also block
  the internal API the Shorts feed uses to compensate, but it is not 100%, and the Shorts shelf
  on the home page does not disappear. Complete blocking requires a force-installed extension.
- **Browser policy applies per browser.** Switching to a browser without the policy defeats both
  Shorts blocking and DoH blocking. Paths other than `chrome` / `edge` are unverified assumptions
  and must be checked directly via a policy page such as `whale://policy`. Firefox is not
  Chromium-based, so this approach does not work there.
- **A user with an administrator account can stop the service at any time.** The child's account
  must be a **Standard user** for this to mean anything.
- The DNS proxy only handles **UDP queries.** TCP DNS queries don't go through the proxy.
- The firewall blocks **outbound only.** An already-running Roblox process's existing connection
  is blocked starting from its next connection attempt.

**Operational behavior**

- Blocking/allowing changes may lag by up to `EvaluationIntervalSeconds` (default 10 seconds).
  A permit granted via a Telegram command takes effect immediately.
- Perceived delay can also come from connections the browser already has open, or its own DNS
  cache. **A video already playing is not cut off immediately.** DNS blocking only stops future
  name lookups; connections already established and video already buffered keep flowing.
  Restarting the browser blocks it at once.
- **Browser policy changes take effect when the browser re-reads its policies.** They land in the
  registry immediately, but an already-open browser may take a while to notice. To check right
  away, restart the browser or hit "Reload policies" on `chrome://policy`.
- **Stopping the service does not lift hosts / firewall blocking.** This is intentional — if
  merely stopping the service lifted blocking, it wouldn't mean anything. To fully remove it,
  run `TimeBlocker.Service.exe cleanup`.
- With `AutoConfigureAdapters` on, the adapter's DNS gets changed. If the service is **force
  killed**, DNS can stay pointed at 127.0.0.1 until it restarts, breaking the internet in the
  meantime. To minimize this, the following are all in place:
  - Service recovery auto-restarts it after 10 seconds (restored on restart)
  - Unclean-shutdown detection restores the adapter's DNS at startup
  - A health check every 2 minutes, on 2 consecutive failures, restores the adapter and falls
    back to hosts
  - The adapter is restored on a clean shutdown too

  If the internet still isn't working, run this with administrator rights:
  ```powershell
  "C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
  ```
  Or switch DNS back to "Obtain DNS server address automatically" in network settings for an
  immediate fix.
- Changing/restoring adapter DNS uses `netsh`, which requires administrator rights. Running in
  console mode without administrator rights fails to change the adapter and falls back to hosts.
  (Since adapters aren't touched in that case either, the internet stays normal.)
- If the IPv6 listener fails to open, the adapter's IPv6 DNS is not changed. In an environment
  with a separately configured IPv6 DNS, that path could bypass the proxy, and a warning is
  logged for it.
- If another program (another DNS server, etc.) is using port 53, the proxy fails to start. In
  the default mode it automatically switches to hosts, then **retries starting the proxy every
  minute.** Once the port is freed, it switches back to proxy mode with no further action needed.
- Starting/stopping/falling back the DNS proxy is all exception-isolated, so a failure there
  never stops the service. Even if the proxy dies, schedule evaluation, firewall blocking, and
  Telegram control keep working.

**Clock manipulation**

- A large system clock change is detected and logged as `WARN`.
  (Judged from the difference between `Environment.TickCount64` and the wall clock.)
- Temporary permits are managed as an absolute UTC expiry time, so turning the clock back doesn't
  extend them.
- That said, **changing the clock to dodge the blocking window itself is not prevented.** Since a
  standard user account can't normally change the system clock, keeping the child's account as a
  standard user is the most reliable defense here.

**Feature scope**

- There's no settings GUI in this first version. Administration is via Telegram, and local checks
  are via the CLI.
- Adding/removing admins over Telegram isn't supported. (`admin list` only views them.)
- Only one remote control provider — Telegram — is implemented. Implementing
  `IRemoteCommandProvider` and registering it in DI would let you add something like Discord.
- If the Telegram API or the internet goes down, only remote control is briefly unavailable —
  **local blocking keeps working normally.**
