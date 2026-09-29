# TimeBlocker

Windows 10 / 11 에서 **시간대별로 YouTube / Roblox 접근을 차단**하는 프로그램입니다.

관리자는 **Telegram 채팅창을 원격 콘솔처럼 사용해서** 상태를 확인하고, 차단 시간을 바꾸고,
필요할 때 일시 허용을 줄 수 있습니다. 차단 대상 PC 에는 설정 GUI 가 없습니다.

```
        [관리자 스마트폰]
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
           접근 제어
```

PC 에는 외부 포트를 열지 않습니다. 서비스가 Telegram 으로 **outbound long polling** 만 합니다.

---

## 1. 프로그램 구조

```
TimeBlocker.sln
├─ src/TimeBlocker.Shared          공용 라이브러리 (Windows 의존 없음에 가까움)
│  ├─ Models/                      BlockTarget, DaySchedule, TemporaryPermit, AccessDecision
│  ├─ Configuration/               TimeBlockerConfig, AppPaths, JsonConfigurationStore
│  ├─ Core/                        ScheduleManager, TemporaryPermitManager, AccessPolicyEngine
│  ├─ Remote/                      RemoteCommandParser, RemoteCommandHandler, ResponseFormatter
│  ├─ Ipc/                         IPC 계약 + Named Pipe 클라이언트
│  └─ Common/                      JsonUtil, SecretProtector(DPAPI), PasswordHash, ISystemClock
│
├─ src/TimeBlocker.Service         Windows Service (핵심)
│  ├─ Worker/                      EnforcementWorker(차단 루프), RemoteProviderWorker
│  ├─ Blocking/                    HostsFileManager, FirewallManager, RobloxLocator,
│  │  └─ Dns/                      DnsProxyServer, DnsMessage, DnsSelfTest,
│  │                                NetworkAdapterDnsConfigurator, AdapterDnsStateStore
│  ├─ Remote/                      IRemoteCommandProvider, TelegramRemoteCommandProvider
│  ├─ Ipc/                         NamedPipeIpcServer
│  ├─ Logging/                     파일 로그 + 로테이션
│  ├─ Security/                    DataDirectoryHardener (ACL)
│  ├─ Maintenance/                 DoctorService(진단), SystemRestoreService(원상복구)
│  └─ Setup/                       set-token / set-admin / cleanup 등 로컬 설정 명령
│
├─ src/TimeBlocker.Admin           로컬 관리자 CLI (보조 도구, GUI 없음)
├─ tests/TimeBlocker.Tests         xunit 단위 테스트
└─ scripts/                        빌드 / 설치 / 제거 / Telegram 설정 / 스모크 테스트
```

### 설계상 중요한 점

| 항목 | 내용 |
|---|---|
| **단일 판정 로직** | "지금 차단인가?" 는 `AccessPolicyEngine` 한 곳에서만 결정한다. DNS / 방화벽 / Telegram / CLI 는 각자 판단하지 않고 이 결과만 쓴다. |
| **판정 우선순위** | 대상 Disabled → ALLOW · 일시 허용 활성 → ALLOW · 차단 스케줄 안 → BLOCK · 그 외 → ALLOW |
| **느슨한 결합** | Telegram 이 죽어도 `EnforcementWorker` 의 차단은 그대로 동작한다. 서로 다른 BackgroundService 로 분리되어 있다. |
| **만료는 절대시각** | 일시 허용은 남은 시간이 아니라 **UTC 절대 만료시각**으로 저장한다. 재부팅·재시작·시간 조작에도 늘어나지 않는다. |
| **원격은 outbound only** | PC 에 수신 포트를 열지 않는다. Named Pipe 는 로컬 전용이며 ACL 로 관리자만 접근 가능하다. |

---

## 2. 필요 환경

- Windows 10 / Windows 11
- .NET 8 (현재 LTS)
  - 빌드: .NET SDK 8.0 이상 (SDK 9 로도 빌드됩니다)
  - 실행: .NET 8 Desktop Runtime — 또는 self-contained 로 게시
- 관리자 권한 (설치 및 설정 시)
- 외부 유료 라이브러리 없음 (NuGet 은 Microsoft 공식 패키지만 사용)

---

## 3. 빌드 방법

### Visual Studio

`TimeBlocker.sln` 을 열고 빌드하면 됩니다.

### 명령줄

```powershell
dotnet build TimeBlocker.sln -c Release
dotnet test  tests\TimeBlocker.Tests\TimeBlocker.Tests.csproj
```

### 게시까지 한 번에

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

### 더 간단하게: .bat 더블클릭

PowerShell 명령을 직접 치기 번거로우면 저장소 루트의 .bat 파일을 쓰면 됩니다.
관리자 권한이 필요한 것들은 **실행하면 알아서 권한 상승 창을 띄웁니다.**

| 파일 | 하는 일 | 권한 상승 |
|---|---|---|
| `build.bat` | 빌드 + 테스트 + publish | 불필요 |
| `install.bat` | Bot Token / User ID 를 물어본 뒤 설치, 끝나면 doctor 자동 실행 | 자동 |
| `doctor.bat` | 상태 종합 점검 | 자동 |
| `dns-restore.bat` | **인터넷이 안 될 때 응급 복구** | 자동 |
| `smoke-test.bat` | 통합 스모크 테스트 | 자동 |
| `configure-telegram.bat` | Telegram 설정 변경 | 자동 |
| `uninstall.bat` | 제거 + 원상복구 | 자동 |

`install.bat` 은 publish 폴더가 없으면 빌드를 먼저 할지 물어봅니다.

### 배포용 패키지 만들기

다른 PC(자녀 PC 등)에 설치할 zip 을 만듭니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1
```

또는 `make-release.bat` 을 실행하면 됩니다.

`release\TimeBlocker-<버전>.zip` 이 생성됩니다. 안에는 실행파일, 설치 스크립트,
`.bat` 실행 파일, README, `설치안내.txt` 가 들어 있고 **소스와 빌드 도구는 들어가지 않습니다.**

대상 PC 에는 .NET SDK 가 필요 없고 **.NET 8 Desktop Runtime** 만 있으면 됩니다.
압축을 푼 뒤 `install.bat` 을 실행하면 설치됩니다.


`publish\` 폴더에 `TimeBlocker.Service.exe` 와 `TimeBlocker.Admin.exe` 가 생성됩니다.

---

## 4. 설치 방법

**관리자 권한 PowerShell** 에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 `
    -BotToken "123456789:AAE..." `
    -AdminUserId 123456789
```

스크립트가 하는 일:

1. `%ProgramFiles%\TimeBlocker` 로 파일 복사
2. 설치 폴더 ACL 설정 (SYSTEM / Administrators 만 쓰기, Users 는 읽기)
3. Bot Token 을 DPAPI 로 암호화해 저장, 관리자 User ID 등록, Telegram 활성화
4. `sc.exe` 로 서비스 등록 — **자동 시작**, LocalSystem 계정
5. 서비스 복구(Recovery) 설정 적용
6. 서비스 시작

### 서비스 복구(Recovery) 설정

설치 스크립트가 다음을 적용합니다. (서비스 속성 → 복구 탭에서 확인 가능)

| 항목 | 값 |
|---|---|
| First failure | Restart the Service (10초 후) |
| Second failure | Restart the Service (30초 후) |
| Subsequent failures | Restart the Service (60초 후) |
| Reset fail count after | 1 day |
| 종료 코드가 0이 아닌 경우에도 복구 적용 | 예 (`failureflag 1`) |

```powershell
sc.exe failure TimeBlocker reset= 86400 actions= restart/10000/restart/30000/restart/60000
sc.exe failureflag TimeBlocker 1
```

**이 설정이 중요한 이유:** DNS 프록시 모드에서 서비스가 죽은 채로 남으면 어댑터 DNS 가
127.0.0.1 을 가리킨 상태가 될 수 있습니다. 서비스가 자동으로 다시 시작되면
시작 시 비정상 종료를 감지해 어댑터 DNS 를 원래대로 되돌립니다.

현재 설정 확인:

```powershell
sc.exe qfailure TimeBlocker
```

Token / User ID 를 나중에 설정하려면 인자 없이 설치한 뒤:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\configure-telegram.ps1 `
    -BotToken "123456789:AAE..." -AdminUserId 123456789
```

### Windows Service 설치 / 삭제 (수동)

```powershell
# 설치
sc.exe create TimeBlocker binPath= "\"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe\"" start= auto obj= LocalSystem
sc.exe failure TimeBlocker reset= 86400 actions= restart/10000/restart/30000/restart/60000
sc.exe start TimeBlocker

# 상태
sc.exe query TimeBlocker

# 삭제 (반드시 cleanup 을 먼저 실행)
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" cleanup
sc.exe stop   TimeBlocker
sc.exe delete TimeBlocker
```

### 제거

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
# 설정/로그까지 모두 삭제하려면
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1 -RemoveData
```

### 제거 시 원상복구 보장

제거와 `cleanup` 은 **같은 공통 코드**(`SystemRestoreService`)를 사용합니다. 순서는 다음과 같습니다.

```
1. 일시 허용 / 상태 정리
2. 저장된 Original DNS 복원      <- 인터넷을 가장 먼저 살린다
3. TimeBlocker hosts 영역 제거
4. TimeBlocker 방화벽 규칙 제거
5. DNS 캐시 비우기
6. Windows Service 제거
7. 남은 상태파일 제거
```

**중간 단계가 실패해도 나머지는 계속 수행하고**, 마지막에 전체 결과를 출력합니다.

```
Permit cleanup    : OK
DNS restore       : OK (1개: Wi-Fi)
Hosts cleanup     : OK
Firewall cleanup  : OK (1개 제거)
DNS cache flush   : OK
State cleanup     : OK
Service removal   : OK

System restored successfully.
```

일부가 실패하면 조치 방법과 함께 표시되고, 스크립트는 종료 코드 `2` 를 돌려줍니다.

```
DNS restore       : FAILED: 일부 어댑터 복구 실패 (Wi-Fi)
Hosts cleanup     : OK
Firewall cleanup  : OK

조치 방법:
- DNS restore: 네트워크 설정에서 해당 어댑터의 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요.

System partially restored. 1 step(s) failed - 위 조치를 확인하세요.
```

안전 규칙:

- hosts 는 `# TIMEBLOCKER BEGIN ~ END` **마커 구간만** 지웁니다. 기존 내용은 건드리지 않습니다.
- 방화벽은 `TimeBlocker_` 로 시작하는 **우리 규칙 이름만** 지웁니다. 사용자의 다른 규칙은 그대로 둡니다.
- DNS 는 저장된 **원래 값**으로 되돌립니다. 무조건 DHCP 로 바꾸지 않으므로, 수동 DNS 를 쓰던 환경의 설정이 보존됩니다.
- 어댑터 DNS 복구가 끝나지 않았으면 상태파일(백업)을 **지우지 않습니다.** 복구 정보를 잃지 않기 위해서입니다.

---

## 5. 관리자 권한이 필요한 이유

| 동작 | 이유 |
|---|---|
| hosts 파일 수정 | `C:\Windows\System32\drivers\etc\hosts` 는 관리자만 쓸 수 있습니다. |
| 방화벽 규칙 생성/삭제 | `netsh advfirewall` 은 관리자 권한이 필요합니다. |
| DNS 프록시 53 포트 바인딩 | 1024 미만 포트 + 네트워크 어댑터 DNS 변경. |
| 데이터 폴더 ACL 설정 | 일반 사용자가 설정/허용 상태를 고치지 못하게 하려면 ACL 변경 권한이 필요합니다. |
| Windows Service 등록 | 서비스 등록/시작은 관리자 권한이 필요합니다. |
| DNS 캐시 flush | 시스템 DNS 확인자 캐시 조작. |

서비스는 **LocalSystem** 계정으로 실행되므로 위 작업을 수행할 수 있습니다.
GUI 가 없고, 로컬 CLI 는 관리자 권한이 있어야만 서비스에 연결할 수 있습니다.

Windows 보안 기능을 우회하거나 프로세스를 숨기지 않습니다. 서비스 목록과 작업 관리자에 정상적으로 보입니다.

---

## 6. Telegram Bot 설정 방법

1. Telegram 에서 **@BotFather** 에게 `/newbot` 을 보내 봇을 만들고 **Bot Token** 을 받습니다.
   (형식: `123456789:AAE-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`)
2. 내 **숫자 User ID** 를 확인합니다. **@userinfobot** 에게 아무 메시지나 보내면 알려줍니다.
   - username 이 아니라 반드시 **숫자 ID** 여야 합니다. username 은 바뀔 수 있어 인증에 쓰지 않습니다.
3. 만든 봇과 **먼저 1:1 대화를 시작**합니다. (`/start` 를 한 번 보내야 봇이 메시지를 받을 수 있습니다)
4. PC 에서 관리자 권한으로 설정합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\configure-telegram.ps1 `
    -BotToken "123456789:AAE..." -AdminUserId 123456789
```

### 보안

- Bot Token 은 **DPAPI(LocalMachine)** 로 암호화되어 저장됩니다. 설정 파일에 평문으로 남지 않습니다.
- Token 은 **어떤 로그에도 출력되지 않습니다.**
- 허용된 숫자 User ID 이외의 메시지는 처리하지 않고 다음만 기록합니다.
  ```
  Unauthorized Telegram access attempt UserId=xxxx
  ```
- **Bot Token 과 관리자 User ID 는 Telegram 명령으로 바꿀 수 없습니다.** PC 앞에서 관리자 권한으로만 변경할 수 있습니다. (`admin list` 로 조회만 가능)

---

## 7. 최초 실행 방법

설치 스크립트가 서비스를 시작하므로 별도 실행은 필요 없습니다. 확인만 하면 됩니다.

```powershell
sc.exe query TimeBlocker
```

Telegram 에서 봇에게 다음을 보냅니다.

```
status
```

응답이 오면 정상입니다.

```
TimeBlocker STATUS

PC          : ONLINE
Service     : RUNNING

Current Time:
2026-09-22 21:20

YouTube     : BLOCKED
Roblox      : BLOCKED

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
```

DNS 프록시가 뜨지 못한 경우에는 이 부분이 이렇게 보입니다.

```
DNS:
DNS Mode      : ProxyWithHostsFallback
DNS Proxy     : FAILED
Fallback      : HOSTS ACTIVE
Last Error    : Cannot bind 127.0.0.1:53 (AddressAlreadyInUse)
```

### 개발 중 콘솔로 실행

```powershell
# 별도 데이터 폴더를 쓰도록 하면 실제 설정을 건드리지 않습니다.
$env:TIMEBLOCKER_DATA = "D:\temp\tbdata"
.\src\TimeBlocker.Service\bin\Release\net8.0-windows\TimeBlocker.Service.exe
```

---

## 8. Telegram 명령어

슬래시(`/`)는 있어도 되고 없어도 됩니다. 대소문자를 구분하지 않습니다.

### 상태

| 명령 | 별칭 | 설명 |
|---|---|---|
| `status` | `s` | 전체 상태 |
| `targets` | | 대상별 상태 (auto / block / unblock) |
| `schedule` | `sch` | 요일별 차단 시간 |
| `domains youtube` | | 차단 도메인 목록 |
| `maxpermit` | | 최대 허용 시간 |
| `admin list` | | 등록된 관리자 User ID |
| `ping` | | 생존 확인 + uptime |
| `version` | | 버전 / 런타임 / OS |
| `help` | `h` | 명령 목록 |

### 일시 허용

```
youtube 30      (yt 30)     YouTube 를 지금부터 30분 허용
roblox 60       (rb 60)     Roblox 를 60분 허용
all 20                      전체를 20분 허용
```

응답:

```
OK
YouTube allowed for 30 minutes.

Start  : 21:15
Expire : 21:45
```

허용 시간이 끝나면 **별도 조작 없이 자동으로 다시 차단**됩니다.

### 스케줄 변경

```
schedule mon 21:00 07:00
schedule mon-thu 21:00 07:00
schedule fri-mon 23:00 08:00     요일 범위는 주를 넘어가도 됩니다
schedule weekday 21:00 07:00     월~금
schedule weekend 22:00 09:00     토,일
schedule sat off                 그 요일은 제한 없음
schedule default 21:00 07:00     전체 요일 기본값
```

시간은 `21:00` 또는 `2100` 형식을 모두 받습니다.

### 대상 상태 — block / unblock / auto

대상마다 상태는 **항상 셋 중 정확히 하나**입니다.

| 상태 | 명령 | 의미 |
|---|---|---|
| 스케줄 (기본값) | `auto youtube` | 스케줄 차단 시간대에만 막힘 |
| 잠금 | `block youtube` | 스케줄과 무관하게 계속 막힘 |
| 열림 | `unblock youtube` | 스케줄과 무관하게 계속 열림 |

대상은 `youtube` / `roblox` / `all` 입니다. 대상을 생략하면 전체에 적용됩니다
(`block` = `block all`).

`targets` 로 확인하면 이렇게 보입니다.

```
YouTube : block   잠금 (항상 막음)
Roblox  : auto    자동 (스케줄대로)
```

`status` 는 결과와 이유를 같이 보여줍니다.

```
YouTube     : BLOCKED (block · 항상 막음)
Roblox      : ALLOW   (auto · 지금은 차단 시간 아님)
```

**상태를 바꾸면 그 대상의 일시 허용도 함께 취소됩니다.** 일시 허용은 스케줄을
덮어쓰는 예외라서, 그대로 두면 방금 내린 명령이 먹지 않는 것처럼 보이기 때문입니다.

> **`block` 은 즉시 잠그고, `auto` 는 스케줄에 맡깁니다.**
> "스케줄을 맞췄는데 왜 안 막히지?" 의 원인은 대부분 그 대상이 `unblock` 상태인 것입니다.
> `status` 가 그 경우 `auto <대상>` 안내를 함께 출력합니다.

#### 일시 허용과의 관계

`youtube 30` 같은 일시 허용은 상태와 별개로 **"지금만 잠깐 열기"** 입니다.
`block` 으로 잠가둔 상태에서도 일시 허용은 동작하며, 시간이 끝나면 원래 상태로
돌아갑니다.

판정 우선순위:

1. `unblock` → 항상 열림
2. 일시 허용이 살아 있음 → 열림
3. `block` → 항상 막힘
4. 스케줄 차단 시간대 → 막힘
5. 그 외 → 열림

#### 옛 명령 호환

1.1 이하에서 쓰던 명령도 그대로 받습니다.

| 옛 명령 | 지금 의미 |
|---|---|
| `lock youtube` | `block youtube` |
| `enable youtube` | `auto youtube` |
| `disable youtube` | `unblock youtube` |

설정파일의 `Enabled` 필드도 처음 읽을 때 자동으로 `Mode` 로 변환되므로,
기존 설치본을 그대로 덮어써도 설정이 유지됩니다.

### 도메인 관리

```
domains youtube
domain add youtube music.youtube.com
domain remove youtube music.youtube.com
```

`http://...`, 공백 포함, `1.2.3.4` 같은 잘못된 형식은 거부합니다.

### 설정

```
maxpermit          현재 최대 허용 시간
maxpermit 90       변경 (1 ~ 720분)
reload             설정파일 재로드 + 정책 재계산
```

### DNS 진단 / 복구

```
dns status         현재 DNS 동작 상태 (status 의 DNS 부분과 동일)
dns test           지금 즉시 self-test 수행
dns restore        어댑터 DNS 를 저장된 원래 설정으로 즉시 복구
```

`dns status` 응답 예:

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

장애 시:

```
DNS Mode           : ProxyWithHostsFallback
DNS Proxy          : FAILED
Adapter DNS        : (원래 설정)
Auto Configure     : ENABLED
DNS Self Test      : FAILED (프록시가 응답하지 않습니다)
Original DNS Saved : NO
Fallback           : HOSTS ACTIVE
Last Error         : 어댑터 DNS 변경 실패: ...
```

`dns test` 응답 예:

```
OK

Proxy direct     : OK (OK)
  allowed (example.com) : resolved
  blocked (googlevideo.com) : blocked
System resolver  : blocked OK
  allowed resolve: OK
Adapter DNS      : Wi-Fi=127.0.0.1
```

**`dns restore` 는 인터넷이 이상할 때의 즉시 대응 수단입니다.**
어댑터 DNS 를 원래대로 되돌리고, 차단은 hosts 방식으로 계속 유지합니다.
프록시 방식으로 돌아가려면 `reload` 를 실행하세요.

### 자동 진단 (doctor)

문제가 생겼을 때 원인을 빠르게 찾기 위한 종합 점검입니다.

```
doctor
```

로컬에서도 실행할 수 있고, **서비스가 멈춰 있어도 동작합니다.**
(모든 점검을 바깥에서 관찰 가능한 방식으로 하기 때문입니다)

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" doctor
```

점검 항목: 관리자 권한 · 서비스 설치/실행 · 53 포트 사용 가능 여부 ·
DNS 프록시 IPv4/IPv6 리스너 · 활성 어댑터 · 어댑터 DNS · 원본 DNS 백업 ·
어댑터가 프록시를 가리키는지 · 정상 도메인 해석 · 차단 도메인 차단 여부 ·
hosts 폴백 상태 · hosts 관리 구간 이상 여부 · 방화벽 규칙 · Roblox 실행파일 ·
Telegram 설정/관리자 ID/API 통신 · 설정파일 검증 · 상태파일 읽기·쓰기 ·
데이터 디렉터리 ACL · 로그 디렉터리 쓰기

출력 예:

```
TimeBlocker Doctor

[PASS] Administrator : SYSTEM
[PASS] Service installed : TimeBlocker
[PASS] Service running : Running
[PASS] Port 53 (UDP) : TimeBlocker 사용 중
[PASS] DNS Proxy IPv4 : 127.0.0.1:53
[PASS] DNS Proxy IPv6 : [::1]:53
[PASS] Active adapter : Wi-Fi
[PASS] Adapter DNS : 127.0.0.1
[PASS] Original DNS backup : Wi-Fi
[PASS] Adapter -> proxy : 127.0.0.1 연결됨
[PASS] Normal DNS resolution : example.com -> 93.184.216.34
[PASS] Blocked domain : googlevideo.com blocked
[PASS] Hosts fallback : INACTIVE (프록시 사용 중)
[PASS] Firewall rules : TimeBlocker_Roblox_Block
[WARN] Roblox executable : 설치되어 있지 않음
[PASS] Telegram API : @MyTimeBlockerBot
[PASS] Data directory ACL : SYSTEM/Administrators 만 쓰기

Result : PASS (1 warning)
```

문제가 있으면 항목별 조치 방법을 함께 출력합니다.

```
조치 방법:
- Service running: 관리자 권한으로 실행: Start-Service TimeBlocker
- Adapter -> proxy: 즉시 실행: TimeBlocker.Service.exe dns-restore
```

종료 코드: PASS/WARN 이면 `0`, FAIL 이 하나라도 있으면 `2`.

서비스가 아예 멈춰서 Telegram / CLI 를 쓸 수 없다면 PC 에서 직접:

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
```

### 잘못된 명령

예외를 던지지 않고 사용법을 돌려줍니다.

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

## 9. 로컬 관리자 CLI

GUI 대신 제공하는 보조 도구입니다. **명령 문법은 Telegram 과 완전히 동일**합니다.

```powershell
# 반드시 "관리자 권한으로 실행"한 터미널에서
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe youtube 30
TimeBlocker.Admin.exe schedule mon 21:00 07:00
TimeBlocker.Admin.exe lock
TimeBlocker.Admin.exe logs 200
```

- CLI 는 핵심 로직을 직접 실행하지 않습니다. **Named Pipe 로 서비스에 명령을 전달만** 합니다.
- 실제 권한 변경은 항상 Windows Service 가 수행합니다.
- Named Pipe DACL 이 SYSTEM / Administrators 에게만 열려 있어, 일반 사용자 계정에서는 **상태 조회조차 불가능**합니다.

---

## 10. 차단 방식

### YouTube — DNS 기반

설정의 `Dns.Mode` 로 방식을 고릅니다.

| Mode | 동작 | 하위 도메인 차단 |
|---|---|---|
| `ProxyWithHostsFallback` **(기본값)** | 127.0.0.1 DNS 프록시를 우선 사용하고, 시작하지 못하거나 치명적 오류가 나면 hosts 로 자동 폴백. | 프록시 동작 중 O / 폴백 중 X |
| `Proxy` | 프록시만 사용. 프록시가 뜨지 못하면 도메인 차단이 적용되지 않음. | O |
| `Hosts` | hosts 파일의 TimeBlocker 관리 구간만 추가/삭제. | X |

### 기본값이 `ProxyWithHostsFallback` 인 이유

YouTube 는 `rr1---sn-ab5l6nz7.googlevideo.com` 같은 **동적 하위 도메인**을 대량으로 사용합니다.
hosts 파일은 와일드카드를 지원하지 않아 이런 주소를 막을 수 없고, 매번 바뀌므로 목록에 미리 넣어둘 수도 없습니다.
그래서 실사용 목적에는 DNS 프록시 방식이 맞고, 프록시를 띄울 수 없는 환경에서만 hosts 로 물러섭니다.

> 이미 설치되어 있고 설정파일에 `"Mode": "Hosts"` 가 적혀 있다면 **그 설정이 그대로 유지됩니다.**
> 바뀐 것은 새로 설치할 때의 기본값뿐이며, 기존 설정을 덮어쓰지 않습니다.
> 프록시 방식으로 바꾸려면 설정파일에서 `Mode` 를 `ProxyWithHostsFallback` 로 고치고
> `TimeBlocker.Admin.exe reload` 를 실행하세요.

### 동작 우선순위

```
DNS Proxy 정상
    ↓
Proxy 기반 차단 사용  (하위 도메인 포함 차단)

DNS Proxy 초기화 실패 또는 치명적 오류
    ↓
Hosts fallback 적용  (정확히 일치하는 도메인만 차단)
```

폴백으로 전환되면 로그에 한계를 분명히 남깁니다.

```
WARN  DNS Proxy unavailable. Hosts fallback activated.
      Wildcard/subdomain blocking capability is limited.
      원인: Cannot bind 127.0.0.1:53 (AddressAlreadyInUse)
```

프록시가 도는 동안에는 **hosts 에 동적 도메인을 대량으로 써넣지 않습니다.** 판정은 전부 프록시가 메모리에서 처리합니다.
폴백 중에도 1분마다 프록시 재시작을 시도하고, 성공하면 자동으로 프록시 방식으로 돌아갑니다.

```
INFO  DNS 프록시 시작: 127.0.0.1:53 (상위 DNS: 1.1.1.1, 8.8.8.8)
INFO  DNS 프록시가 복구되었습니다. hosts 폴백을 해제합니다.
```

### 어댑터 DNS 변경 안전 절차

프록시 방식이 실제로 효과를 내려면 네트워크 어댑터의 DNS 가 127.0.0.1 을 가리켜야 합니다
(`Dns.AutoConfigureAdapters = true`). 이 작업은 잘못되면 **PC 인터넷이 통째로 끊길 수 있으므로**
아래 순서를 엄격히 지킵니다.

```
1. DNS Proxy 시작
       ↓
2. 127.0.0.1 에 직접 질의해 self-test
     - 허용 도메인(example.com)이 응답하는가
     - 차단 도메인이 우리 NXDOMAIN 으로 막히는가
       ↓  실패하면 여기서 중단 (어댑터를 건드리지 않음)
3. 원래 DNS 설정을 디스크에 저장   ← 저장 실패 시에도 중단
       ↓
4. 어댑터 DNS 를 127.0.0.1 (+ ::1) 로 변경
       ↓
5. DNS 캐시 비우고 시스템 확인자로 재검증
     - 차단 도메인이 실제로 막히는가
     - 허용 도메인이 실제로 해석되는가
       ↓  실패하면 즉시 원래 설정으로 롤백
6. 완료
```

**어떤 단계에서 실패해도 어댑터는 원래 상태로 남습니다.** 차단은 hosts 폴백으로 계속 동작합니다.

#### 원래 설정 저장

변경 전에 `%ProgramData%\TimeBlocker\state\adapter-dns.json` 에 저장합니다.

```jsonc
{
  "Adapters": [
    {
      "Name": "Wi-Fi",
      "Ipv4Dhcp": true,          // DHCP 자동 DNS 였는지
      "Ipv4Servers": [],         // 수동 DNS 였다면 그 목록
      "Ipv6Dhcp": true,
      "Ipv6Servers": [],
      "Ipv4Changed": true,       // 우리가 실제로 바꾼 항목
      "Ipv6Changed": true
    }
  ],
  "ProcessId": 1234,
  "AppliedAtUtc": "2026-09-22T12:00:00+00:00"
}
```

- 원래 설정은 **레지스트리에서 직접 읽습니다.** (`netsh` 출력 파싱은 Windows 표시 언어에 따라
  달라져 한국어 Windows 에서 깨질 수 있습니다)
- 이 파일은 PC 재부팅 후에도 남습니다.
- **정상적으로 원복하면 파일을 지웁니다.** 따라서 시작 시 이 파일이 남아 있으면
  이전 실행이 비정상 종료된 것으로 판단합니다.
- 사람이 읽을 수 있는 형식이라, 최악의 경우 이 파일을 보고 수동 복구할 수 있습니다.

#### 비정상 종료 복구

서비스 시작 시 가장 먼저 수행합니다. 프록시를 초기화하기 **전에** 안전 상태로 되돌립니다.

```
서비스 시작
   ↓
adapter-dns.json 이 남아 있는가?
   ├─ NO  → 정상 종료였음. 그대로 진행
   └─ YES → 이전 실행이 비정상 종료됨
              ↓
            저장된 원래 DNS 설정으로 복구
              ↓
            DNS 캐시 비우기
              ↓
            그 다음에 DNS Proxy 초기화
```

복구에 실패한 어댑터가 있으면 **상태 파일을 지우지 않고 유지**해서 다음 시작 때 다시 시도합니다.

#### 실행 중 감시 (fail-safe)

프록시가 도는 동안 2분마다 self-test 를 수행합니다.

```
health check 실패 (연속 2회)
   ↓
어댑터 DNS 를 원래 설정으로 복구   ← 인터넷을 먼저 살린다
   ↓
Hosts fallback 활성화             ← 차단은 계속 유지
   ↓
로그 기록
```

인터넷이 끊긴 상태로 방치하지 않습니다.

> 참고: 상위 DNS 에 닿지 못하는 경우(인터넷 자체가 끊긴 경우)는 프록시 고장과 구분합니다.
> 이때는 어댑터를 롤백하지 않고 경고만 남깁니다. 롤백해도 인터넷이 살아나지 않기 때문입니다.

#### 정상 종료 시 동작 (차단 유지와의 충돌 해결)

서비스를 멈출 때 두 가지 요구가 충돌합니다.

| 요구 | 필요한 동작 |
|---|---|
| 서비스가 없는 동안 인터넷이 끊기면 안 됨 | 어댑터 DNS 를 원래대로 되돌려야 함 |
| 서비스를 꺼서 차단을 풀 수 있으면 안 됨 | 차단이 유지되어야 함 |

TimeBlocker 는 둘 다 만족시킵니다.

```
정상 종료
   ↓
1. 어댑터 DNS 를 원래 설정으로 복구   (인터넷 보장)
2. DNS Proxy 중지
3. 차단 중이던 도메인을 hosts 에 기록  (차단 유지)
4. 방화벽 규칙은 그대로 유지
```

즉 **서비스가 꺼져 있는 동안에는 hosts 방식으로 차단이 계속됩니다.**
다만 hosts 는 와일드카드를 지원하지 않으므로 하위 도메인 차단은 제한됩니다.
서비스가 다시 시작되면 프록시 방식으로 복귀합니다.

#### IPv6 DNS 경로

IPv4 DNS 만 127.0.0.1 로 바꾸면 Windows 가 IPv6 DNS 서버로 질의해 **프록시를 우회**할 수 있습니다.
그래서 프록시는 `127.0.0.1` 과 `[::1]` 양쪽에서 질의를 받고, 어댑터의 IPv4/IPv6 DNS 를 함께 바꿉니다.

- `Dns.ConfigureIpv6` (기본값 `true`) 로 끌 수 있습니다.
- IPv6 리스너를 열지 못한 경우에는 **어댑터 IPv6 DNS 를 바꾸지 않습니다.**
  (받는 쪽이 없는데 `::1` 로 바꾸면 IPv6 이름 해석이 완전히 막힙니다)
  이 경우 우회 가능성을 경고 로그로 남깁니다.

#### 대상 어댑터 선택

아무 어댑터나 건드리지 않습니다. 다음 조건을 모두 만족하는 것만 변경합니다.

- 연결됨(`Up`)
- 유선(Ethernet) 또는 무선(Wireless80211)
- 루프백 / 터널 제외
- 가상 어댑터 제외 (VMware, Hyper-V, VirtualBox, WSL, Npcap, VPN, TAP, Bluetooth 등)
- 게이트웨이가 있는 어댑터 우선 (실제로 인터넷에 붙어 있는 것)

변경한 어댑터와 각각의 원래 설정은 개별로 저장됩니다.

hosts 방식은 **기존 내용을 손상시키지 않고** 마커 구간만 관리합니다.

```
# TIMEBLOCKER BEGIN
# 이 구간은 TimeBlocker 가 자동으로 관리합니다. 직접 수정하지 마세요.
0.0.0.0 youtube.com
::1 youtube.com
# TIMEBLOCKER END
```

DNS 프록시 방식의 동작:

```
Application
   ↓
127.0.0.1 Local DNS Proxy
   ↓
차단 대상인가?
 ├─ YES → NXDOMAIN
 └─ NO  → Upstream DNS(1.1.1.1 / 8.8.8.8)로 전달
```

### 하위 도메인 판정 규칙

프록시는 정확한 일치뿐 아니라 하위 도메인 여부까지 봅니다. 판정은 다음 규칙 하나뿐입니다.

```
domain == blockedDomain
OR
domain.EndsWith("." + blockedDomain)
```

부분 문자열 검색(`Contains`)은 쓰지 않습니다. 그렇게 하면 관계없는 도메인까지 막히기 때문입니다.

설정에 `googlevideo.com` 하나만 있으면 다음과 같이 판정됩니다.

| 질의 도메인 | 결과 | 이유 |
|---|---|---|
| `googlevideo.com` | **BLOCK** | 정확히 일치 |
| `rr1---sn-xxxx.googlevideo.com` | **BLOCK** | 하위 도메인 |
| `rr2---sn-xxxx.googlevideo.com` | **BLOCK** | 하위 도메인 |
| `abc.googlevideo.com` | **BLOCK** | 하위 도메인 |
| `notgooglevideo.com` | ALLOW | 앞에 점이 없음 (부분 문자열일 뿐) |
| `googlevideo.com.evil.com` | ALLOW | 접미사가 아님 |

기본 설정에는 다음 계열이 들어 있고, 각각 하위 도메인까지 차단됩니다.

```
youtube.com
youtu.be
googlevideo.com
ytimg.com
youtubei.googleapis.com
```

즉 `music.youtube.com`, `www.youtube.com` 은 `youtube.com` 하나로 함께 막히고,
`myyoutube.com` 은 막히지 않습니다.

도메인 목록은 코드에 하드코딩되어 있지 않고 설정파일 / Telegram 명령으로 관리합니다.

### Roblox — DNS + 방화벽

- DNS 차단 (위와 동일)
- Windows 방화벽 아웃바운드 차단
  - 규칙 이름: `TimeBlocker_Roblox_Block`
  - 실행파일 자동 탐색: `C:\Users\*\AppData\Local\Roblox\Versions\*`, Bloxstrap, `Program Files\Roblox`
  - 차단 해제 시 규칙을 **삭제**하고, 다시 차단 시간이 되면 자동으로 재생성합니다.
  - Roblox 가 업데이트되어 경로가 바뀌어도 매번 다시 탐색하므로 따라갑니다.

### DNS 캐시

차단/허용 상태가 실제로 **바뀐 경우에만** 캐시를 비웁니다.
`dnsapi.dll` 의 `DnsFlushResolverCache` 를 직접 호출하고, 실패하면 `ipconfig /flushdns` 로 폴백합니다.

---

## 11. 설정파일

위치: `%ProgramData%\TimeBlocker\timeblocker.config.json`
(환경변수 `TIMEBLOCKER_DATA` 로 위치를 바꿀 수 있습니다 — 개발/테스트용)

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
    "AutoConfigureAdapters": true,     // 어댑터 DNS 를 127.0.0.1 로 자동 변경
    "ConfigureIpv6": true,             // IPv6 DNS(::1)도 함께 변경
    "SelfTestDomain": "example.com",   // self-test 에 쓸 "해석되어야 하는" 도메인
    "FlushCacheOnChange": true
  },
  "YouTube": {
    "Enabled": true,
    "UseDnsBlocking": true,
    "UseFirewallBlocking": false,
    "Domains": [ "youtube.com", "www.youtube.com", "youtu.be", "googlevideo.com", "ytimg.com" ]
  },
  "Roblox": {
    "Enabled": true,
    "UseDnsBlocking": true,
    "UseFirewallBlocking": true,
    "Domains": [ "roblox.com", "rbxcdn.com" ],
    "ProcessNames": [ "RobloxPlayerBeta.exe", "RobloxStudioBeta.exe" ]
  },
  "TemporaryPermit": { "MaxMinutes": 120, "MinMinutes": 1 },
  "Telegram": {
    "Enabled": true,
    "ProtectedBotToken": "(DPAPI 로 암호화된 값)",
    "AllowedUserIds": [ 123456789 ],
    "PollTimeoutSeconds": 30
  },
  "Logging": { "MinimumLevel": "INFO", "MaxFileSizeMb": 8, "RetainDays": 30 },
  "Enforcement": { "EvaluationIntervalSeconds": 10, "TimeJumpThresholdMinutes": 5 }
}
```

- 저장은 **임시 파일에 쓰고 교체(atomic)** 하므로 도중에 전원이 꺼져도 기존 파일이 깨지지 않습니다.
- 파일이 손상되면 `.broken` 으로 백업하고 **기본 설정으로 계속 동작**합니다. 서비스는 죽지 않습니다.
- 데이터 폴더는 SYSTEM / Administrators 만 쓰기 가능하도록 ACL 이 적용됩니다.

---

## 12. 로그

위치: `%ProgramData%\TimeBlocker\logs\TimeBlocker-YYYY-MM-DD.log`

- 레벨: `TRACE` / `DEBUG` / `INFO` / `WARN` / `ERROR`
- 날짜별 파일 + 크기 초과 시 롤링(`TimeBlocker-2026-09-22.1.log`) + `RetainDays` 경과 파일 자동 삭제
- 모든 원격 명령이 감사 로그로 남습니다.

```
2026-09-22 21:00:00 [INFO ] BlockingCoordinator: YouTube BLOCK (InBlockingSchedule)
2026-09-22 21:00:00 [INFO ] BlockingCoordinator: Roblox BLOCK (InBlockingSchedule)
2026-09-22 21:15:02 [INFO ] RemoteCommandHandler: REMOTE COMMAND | Telegram:123456789 | Command=youtube 30 | Result=SUCCESS
2026-09-22 21:15:02 [INFO ] TemporaryPermitManager: 일시 허용: YouTube 30분 (요청자: Telegram:123456789, 만료: 2026-09-22 21:45)
2026-09-22 21:20:11 [INFO ] RemoteCommandHandler: REMOTE COMMAND | Telegram:123456789 | Command=schedule mon 22:00 07:00 | Result=SUCCESS
2026-09-22 21:45:03 [INFO ] TemporaryPermitManager: 일시 허용 만료: YouTube
2026-09-22 21:45:03 [INFO ] BlockingCoordinator: YouTube BLOCK (InBlockingSchedule)
```

Bot Token 은 절대 기록되지 않습니다.

조회:

```powershell
TimeBlocker.Admin.exe logs 200
```

---

## 13. 테스트 방법

### 단위 테스트

```powershell
dotnet test tests\TimeBlocker.Tests\TimeBlocker.Tests.csproj
```

커버하는 내용:

- 일반 시간 범위 판정 (`09:00~17:00`)
- **자정을 넘어가는 범위 판정** (`21:00~07:00` → 20:59 ALLOW / 21:00 BLOCK / 00:30 BLOCK / 06:59 BLOCK / 07:00 ALLOW)
- 요일별 설정, 금요일 밤이 토요일 새벽까지 이어지는 경우
- 일시 허용 부여 / 만료 경계 (21:10 에 30분 → 21:39 ALLOW, 21:40 BLOCK)
- **재부팅 복구** (21:00 에 60분 허용 → 21:20 재시작 → 22:00 에 정확히 차단)
- 최대 허용 시간 초과 거부, 같은 대상 재허용 시 누적되지 않고 교체
- Policy Engine 우선순위 (Disabled > Permit > Schedule)
- Telegram 명령 파서 전체 (alias, 요일 범위, 도메인 형식 검증, 잘못된 입력)
- 명령 핸들러 시나리오 (설정 저장 → 정책 재계산 → 응답)
- hosts 파일 조작 (기존 내용 보존, 마커 구간만 교체, 손상 복구)
- DNS 메시지 파싱 / NXDOMAIN 생성 / 하위 도메인 매칭

### 실제 Telegram 연동 테스트 절차

1. 설치와 Telegram 설정을 마칩니다. (4장, 6장)
2. 서비스가 Running 인지 확인합니다.
   ```powershell
   sc.exe query TimeBlocker
   ```
3. Telegram 에서 봇과의 대화창을 열고 아래를 **순서대로** 보냅니다.

| # | 보낼 메시지 | 기대 결과 |
|---|---|---|
| 1 | `status` | `Service : RUNNING`, 현재 시각, YouTube/Roblox 상태, 스케줄, `Temporary Permit: None` |
| 2 | `schedule mon-thu 21:00 07:00` | `OK` + 월~목이 `21:00-07:00` 으로 바뀐 목록 |
| 3 | `youtube 30` | `OK` + `YouTube allowed for 30 minutes.` + Start / Expire 시각 |
| 4 | `status` | `YouTube : ALLOW`, `Temporary Permit:` 에 `YouTube until HH:mm (30 min left)` |
| 5 | `block youtube` | `OK` + 일시 허용 취소 안내 + 현재 상태 |
| 6 | `status` | `Temporary Permit: None`. 차단 시간대라면 `YouTube : BLOCKED` |

> 3~6 번에서 상태 변화를 눈으로 확인하려면 **차단 시간대 안에서** 테스트하세요.
> 지금 바로 확인하고 싶다면 `schedule default 00:00 23:59` 로 하루 종일 차단으로 바꾼 뒤
> 테스트하고, 끝나고 원래 값으로 되돌리면 됩니다.

4. 실제 차단 확인
   - 브라우저에서 `https://www.youtube.com` 접속 → 접속 불가
   - `youtube 5` 를 보낸 뒤 다시 접속 → 접속 가능
   - 5분 뒤 자동으로 다시 차단
   - 브라우저 캐시 때문에 즉시 반영되지 않으면 탭을 새로 열거나 브라우저를 재시작하세요.

5. 재부팅 복구 확인
   - `youtube 60` 을 보내고 만료 시각을 기억합니다.
   - PC 를 재부팅합니다.
   - 부팅 후 `status` → 같은 만료 시각이 그대로 남아 있어야 합니다.
   - 만료 시각이 지나면 아무 조작 없이 다시 차단되어야 합니다.

6. 권한 확인 (다른 Telegram 계정으로)
   - 등록되지 않은 계정으로 봇에게 `status` 를 보냅니다.
   - 아무 응답이 없어야 하고, 로그에 다음이 남습니다.
     ```
     Unauthorized Telegram access attempt UserId=xxxx
     ```

### 어댑터 DNS 안전 절차 검증 (관리자 권한 필요)

`Dns.AutoConfigureAdapters = true` 로 실제 설치한 뒤, **관리자 권한 PowerShell** 에서
아래 시나리오를 순서대로 확인합니다.

각 단계 전후로 실제 어댑터 DNS 를 확인하세요.

```powershell
Get-DnsClientServerAddress -AddressFamily IPv4 |
    Where-Object { $_.ServerAddresses.Count -gt 0 } |
    Format-Table InterfaceAlias, ServerAddresses
```

#### 시나리오 1. Proxy 시작 성공 → DNS 변경

```powershell
Start-Service TimeBlocker
Start-Sleep -Seconds 10
TimeBlocker.Admin.exe dns status
```

기대 결과:

```
DNS Proxy          : RUNNING
Adapter DNS        : 127.0.0.1
DNS Self Test      : OK
Original DNS Saved : YES
Fallback           : NOT ACTIVE
```

- `Get-DnsClientServerAddress` 로 실제 어댑터가 `127.0.0.1` 인지 확인
- `%ProgramData%\TimeBlocker\state\adapter-dns.json` 이 생성되고 **원래 DNS 가 들어 있는지** 확인
- 인터넷이 정상 동작하는지 확인 (브라우저로 아무 사이트 접속)

#### 시나리오 2. Proxy 시작 실패 → DNS 변경하지 않음

53 포트를 미리 점유한 상태로 서비스를 시작합니다.

```powershell
Stop-Service TimeBlocker
# 별도 창에서 53 포트를 잡아 둡니다
$ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Loopback, 53)
$holder = New-Object System.Net.Sockets.UdpClient($ep)

Start-Service TimeBlocker
Start-Sleep -Seconds 10
TimeBlocker.Admin.exe dns status
```

기대 결과:

```
DNS Proxy          : FAILED
Adapter DNS        : (원래 설정)
Original DNS Saved : NO
Fallback           : HOSTS ACTIVE
```

- **어댑터 DNS 가 전혀 바뀌지 않아야 합니다.** (인터넷 정상)
- 로그에 `DNS Proxy unavailable. Hosts fallback activated.` 가 남아야 합니다.
- 포트를 풀면 1분 안에 프록시가 복구되고 `RUNNING` 으로 바뀝니다.

```powershell
$holder.Close()
Start-Sleep -Seconds 70
TimeBlocker.Admin.exe dns status
```

#### 시나리오 3. DNS 변경 후 self-test 실패 → 원복

상위 DNS 를 닿을 수 없는 주소로 바꾸고 프록시가 응답하지 못하게 만듭니다.

```powershell
TimeBlocker.Admin.exe dns test     # 변경 전 정상 확인
# 설정파일에서 UpstreamServers 를 ["192.0.2.1"] (도달 불가 주소)로 변경
TimeBlocker.Admin.exe reload
Start-Sleep -Seconds 15
TimeBlocker.Admin.exe dns status
```

기대 결과: self-test 가 실패하면 어댑터를 변경하지 않거나, 이미 변경했다면 원복하고
`Fallback : HOSTS ACTIVE` 로 전환됩니다. **인터넷이 끊긴 채로 남지 않아야 합니다.**

설정을 원래대로 되돌리고 `reload` 하세요.

#### 시나리오 4. 정상 서비스 종료

```powershell
Stop-Service TimeBlocker
Get-DnsClientServerAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, ServerAddresses
Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String TIMEBLOCKER -Context 0,3
```

기대 결과:

- 어댑터 DNS 가 **원래 설정으로 복구됨** → 인터넷 정상
- `adapter-dns.json` 이 **삭제됨**
- 차단 시간대였다면 hosts 에 `# TIMEBLOCKER BEGIN` 구간이 남아 **차단은 계속 유지**

#### 시나리오 5. 강제 종료 후 재시작

```powershell
Start-Service TimeBlocker
Start-Sleep -Seconds 10
Stop-Process -Name TimeBlocker.Service -Force     # 비정상 종료
Get-DnsClientServerAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, ServerAddresses
```

이 시점에는 어댑터 DNS 가 `127.0.0.1` 로 남아 인터넷이 되지 않을 수 있습니다. (의도된 상황)
서비스 복구 설정에 의해 10초 후 자동 재시작되며, 재시작 시 복구됩니다.

```powershell
Start-Sleep -Seconds 20
Get-Service TimeBlocker        # Running 으로 돌아와야 함
TimeBlocker.Admin.exe dns status
```

로그에 다음이 남아야 합니다.

```
WARN  이전 실행이 정상적으로 종료되지 않았습니다. 어댑터 DNS 원본 설정이 남아 있어 먼저 복구합니다.
INFO  어댑터 DNS 를 원래 설정으로 복구했습니다: Wi-Fi (...)
INFO  비정상 종료 복구 완료
```

자동 재시작이 안 될 때의 수동 복구:

```powershell
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
```

#### 시나리오 6. PC 재부팅

```powershell
Restart-Computer
```

부팅 후:

- 서비스가 자동 시작되어야 합니다 (`Get-Service TimeBlocker`)
- 인터넷이 정상이어야 합니다
- `TimeBlocker.Admin.exe dns status` 가 `RUNNING` / `Adapter DNS : 127.0.0.1`
- 재부팅 전에 일시 허용이 남아 있었다면 만료 시각이 그대로 복원되어야 합니다

재부팅 도중 비정상 종료로 처리되었다면 위 시나리오 5 의 복구 로그가 남습니다.

#### 시나리오 7. cleanup

```powershell
Stop-Service TimeBlocker
"C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" cleanup
```

기대 결과:

- hosts 의 TIMEBLOCKER 구간 제거
- `TimeBlocker_*` 방화벽 규칙 제거
- **어댑터 DNS 가 원래 설정으로 복구** (무조건 DHCP 로 바꾸지 않고 저장된 값으로 되돌림)
- 마지막에 현재 어댑터 DNS 를 출력하므로 눈으로 확인 가능

#### 시나리오 8. Wi-Fi / Ethernet 각각

유선과 무선을 번갈아 연결하며 확인합니다.

```powershell
# Wi-Fi 만 연결한 상태에서
TimeBlocker.Admin.exe dns status     # Adapters 에 Wi-Fi 만 나와야 함

# 유선을 연결하고 Wi-Fi 를 끈 뒤
TimeBlocker.Admin.exe reload
TimeBlocker.Admin.exe dns status     # Adapters 에 이더넷이 나와야 함
```

확인할 점:

- 연결되지 않은(`Down`) 어댑터는 대상에서 제외됩니다.
- 가상 어댑터(VMware, Hyper-V, VirtualBox, WSL, Npcap, VPN 등)는 제외됩니다.
- 로그 레벨을 `DEBUG` 로 두면 `어댑터 제외: ...` 로 어떤 것이 왜 빠졌는지 볼 수 있습니다.

### 통합 스모크 테스트 (실제 설치 환경)

설치를 마친 뒤 **관리자 권한 PowerShell** 에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```

> ★ 이 스크립트는 읽기 전용이 아닙니다. 일시 허용을 만들고 취소하며,
> 어댑터 DNS 를 되돌렸다가 다시 적용합니다. 실행 전 확인을 요구합니다.
> (`-Force` 로 확인을 건너뛸 수 있습니다)

14개 항목을 순서대로 점검합니다.

| # | 항목 |
|---|---|
| 1 | Service 실행 상태 |
| 2 | DNS Proxy 상태 |
| 3 | 활성 어댑터 DNS 가 127.0.0.1 인지 |
| 4 | 정상 인터넷 DNS 해석 |
| 5 | YouTube 도메인 차단 |
| 6 | `googlevideo.com` 하위 도메인 차단 |
| 7 | 유사 도메인(`notgooglevideo.com`)은 차단되지 않음 |
| 8 | 일시 허용 생성 |
| 9 | 허용 중 YouTube DNS 열림 |
| 10 | 허용 취소 후 재차단 |
| 11 | `dns restore` 동작 |
| 12 | DNS Proxy 재적용 |
| 13 | 방화벽 규칙 |
| 14 | Telegram 연결 |

**안전장치:** 중간에 정상 도메인 해석이 실패하면 즉시 중단하고 Original DNS 를 자동 복원합니다.
스크립트가 어떤 이유로 중단되어도 `finally` 에서 일시 허용을 취소하고 스케줄을 되돌립니다.

현재가 차단 시간대가 아니면 5~10번 테스트를 위해 임시로 하루 종일 차단으로 바꾸고,
끝나면 `21:00~07:00` 으로 되돌립니다.

결과:

```
TimeBlocker Integration Test

 Tests : 14
 PASS  : 14
 FAIL  : 0

RESULT: READY
```

실패 시에는 실패 항목과 `doctor` 실행 안내를 함께 출력합니다.
종료 코드: `0` READY, `1` NOT READY, `2` ABORTED(인터넷 장애로 중단).

### 로컬에서 Telegram 없이 명령 테스트

```powershell
# 관리자 권한 터미널
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe schedule mon-thu 21:00 07:00
TimeBlocker.Admin.exe youtube 30
TimeBlocker.Admin.exe status
TimeBlocker.Admin.exe block youtube
TimeBlocker.Admin.exe status
```

Telegram 과 완전히 같은 명령 처리기를 사용하므로 동작이 동일합니다.

---

## 14. 알려진 제한사항

**차단 회피 관련**

- **hosts 폴백 중에는 하위 도메인을 막지 못합니다.** 기본 모드(`ProxyWithHostsFallback`)에서
  프록시가 정상이면 `googlevideo.com` 하나로 `rr1---sn-xxx.googlevideo.com` 까지 막히지만,
  프록시가 뜨지 못해 hosts 로 폴백하면 정확히 일치하는 도메인만 막힙니다.
  `status` 의 `Fallback : HOSTS ACTIVE` 로 이 상태를 확인할 수 있고, 로그에도 WARN 으로 남습니다.
  주로 53 포트를 다른 DNS 프로그램이 쓰고 있거나 서비스가 관리자 권한이 아닐 때 발생합니다.
- **DoH(DNS over HTTPS) 를 쓰면 DNS 차단이 우회됩니다.** Chrome / Edge / Firefox 의
  "보안 DNS" 기능을 끄거나, 그룹 정책으로 비활성화해야 합니다.
- **VPN / 프록시 / 모바일 테더링** 으로 우회할 수 있습니다. 이 프로그램의 범위를 벗어납니다.
- **관리자 계정을 가진 사용자는 언제든 서비스를 멈출 수 있습니다.**
  자녀 계정을 반드시 **표준 사용자**로 만들어야 의미가 있습니다.
- DNS 프록시는 **UDP 질의만** 처리합니다. TCP DNS 질의는 프록시를 거치지 않습니다.
- 방화벽은 **아웃바운드만** 차단합니다. 이미 실행 중인 Roblox 프로세스의 기존 연결은
  다음 연결 시도부터 막힙니다.

**동작 관련**

- 차단/허용 반영은 최대 `EvaluationIntervalSeconds`(기본 10초) 만큼 늦어질 수 있습니다.
  Telegram 명령으로 준 허용은 즉시 반영됩니다.
- 브라우저가 이미 열어둔 연결이나 자체 DNS 캐시 때문에 체감 반영이 늦을 수 있습니다.
- **서비스를 중지해도 hosts / 방화벽 차단은 유지됩니다.** 의도된 동작입니다.
  (서비스를 끄는 것만으로 차단이 풀리면 의미가 없습니다.) 완전히 해제하려면
  `TimeBlocker.Service.exe cleanup` 을 실행하세요.
- `AutoConfigureAdapters` 가 켜져 있으면 어댑터 DNS 를 변경합니다.
  서비스가 **강제 종료**되면 다시 시작될 때까지 DNS 가 127.0.0.1 로 남아 인터넷이 안 될 수 있습니다.
  이를 줄이기 위해 다음을 적용해 두었습니다.
  - 서비스 복구 설정으로 10초 후 자동 재시작 (재시작 시 복구)
  - 시작 시 비정상 종료 감지 후 어댑터 DNS 원복
  - 실행 중 2분 주기 health check 실패 시 어댑터 원복 + hosts 폴백
  - 정상 종료 시 어댑터 원복

  그래도 인터넷이 되지 않으면 관리자 권한으로 다음을 실행하세요.
  ```powershell
  "C:\Program Files\TimeBlocker\TimeBlocker.Service.exe" dns-restore
  ```
  또는 네트워크 설정에서 DNS 를 "자동으로 DNS 서버 주소 받기"로 바꾸면 즉시 복구됩니다.
- 어댑터 DNS 변경/복구는 `netsh` 를 사용하므로 관리자 권한이 필요합니다.
  관리자 권한 없이 콘솔 모드로 실행하면 어댑터 변경이 실패하고 hosts 폴백으로 동작합니다.
  (이때도 어댑터는 건드리지 않으므로 인터넷은 정상입니다)
- IPv6 리스너를 열지 못하면 어댑터 IPv6 DNS 를 바꾸지 않습니다.
  IPv6 DNS 가 별도로 설정된 환경에서는 그 경로로 프록시가 우회될 수 있고, 경고 로그가 남습니다.
- 53 포트를 다른 프로그램(다른 DNS 서버 등)이 쓰고 있으면 프록시 시작에 실패합니다.
  기본 모드면 자동으로 hosts 로 전환되고, 이후 **1분마다 프록시 재시작을 시도**합니다.
  포트가 풀리면 별도 조작 없이 프록시 방식으로 되돌아갑니다.
- DNS 프록시의 시작/종료/폴백 전환은 모두 예외가 격리되어 있어, 실패해도 서비스가 멈추지 않습니다.
  프록시가 죽어도 스케줄 판정과 방화벽 차단, Telegram 제어는 그대로 동작합니다.

**시간 조작 관련**

- 시스템 시간이 크게 변경되면 감지해서 로그에 `WARN` 으로 남깁니다.
  (`Environment.TickCount64` 와 벽시계의 차이로 판단)
- 일시 허용은 UTC 절대 만료시각으로 관리하므로 시간을 되돌려도 누적되지 않습니다.
- 다만 **시간을 바꿔 차단 시간대를 피하는 것 자체는 막지 못합니다.**
  표준 사용자 계정은 기본적으로 시스템 시간을 바꿀 수 없으므로,
  자녀 계정을 표준 사용자로 두는 것이 가장 확실한 대응입니다.

**기능 범위**

- 1차 버전에는 설정 GUI 가 없습니다. 관리는 Telegram, 로컬 점검은 CLI 로 합니다.
- Telegram 으로 관리자 추가/삭제는 지원하지 않습니다. (`admin list` 로 조회만)
- 원격 제어 제공자는 Telegram 하나만 구현되어 있습니다.
  `IRemoteCommandProvider` 를 구현하고 DI 에 등록하면 Discord 등을 추가할 수 있습니다.
- Telegram API 장애나 인터넷 끊김 시 원격 제어만 잠시 안 되고, **로컬 차단은 정상 동작**합니다.
