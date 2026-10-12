# Unreal Team Project Portfolio

Unreal Engine 멀티플레이 팀 프로젝트에서 구현한 게임플레이 시스템과 제작 도구를 포트폴리오 열람용으로 정리한 저장소입니다.

프로젝트는 Unreal Engine 5.6.1, C++, Blueprint, .NET 8을 사용했습니다. C++ 코드는 원본 프로젝트의 Public/Private 구조와 의존성을 시스템 단위로 재구성한 발췌본이며, 직접 구현한 기능의 노드를 검토할 수 있도록 무기 Blueprint 12개와 조준 AnimBP 2개도 포함했습니다.

> 이 저장소는 C++ 코드와 Blueprint 노드 리뷰용 포트폴리오입니다. 원본 프로젝트의 전체 에셋, 플러그인 설정, Blueprint, 모듈 의존성이 모두 포함되어 있지 않아 독립 빌드나 실행을 지원하지 않습니다.

## System Map

<table>
  <thead>
    <tr>
      <th width="140">영역</th>
      <th width="220">시스템</th>
      <th>확인할 내용</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="140">무기</td>
      <td width="220"><a href="./Blueprints/Weapon">Weapon Blueprint</a></td>
      <td>공통 발사 흐름, 무기별 발사 처리, 투사체 생성과 충돌, 효과와 사운드 연동</td>
    </tr>
    <tr>
      <td width="140">애니메이션</td>
      <td width="220"><a href="./Blueprints/WeaponAimAnimation">Weapon Aim AnimBP</a></td>
      <td>플라즈마건, 로켓런처의 조준 방향과 무기 자세를 맞추는 AnimGraph</td>
    </tr>
    <tr>
      <td width="140">외부&nbsp;데이터</td>
      <td width="220"><a href="./JsonAssetSync">JsonAssetSync</a></td>
      <td>Reflection 기반 Manifest/Schema Export, Editor, Runtime 적용 분리, 에셋 저장 시 JSON 기록</td>
    </tr>
    <tr>
      <td width="140">제작&nbsp;도구</td>
      <td width="220"><a href="./DataEditor">DataEditor</a></td>
      <td>Manifest를 읽는 .NET 8 데이터 편집 도구, JSON/CSV 편집</td>
    </tr>
    <tr>
      <td width="140">제작&nbsp;도구</td>
      <td width="220"><a href="./GameplayValidator">GameplayValidator</a></td>
      <td>Portal, JumpPad, PathLink의 레벨 배치 오류를 Provider 구조로 검사</td>
    </tr>
    <tr>
      <td width="140">AI&nbsp;이동</td>
      <td width="220"><a href="./PathLink">PathLink</a></td>
      <td>Portal, JumpPad, Jump, Drop을 포함하는 Static Graph와 Dijkstra 경로 탐색</td>
    </tr>
    <tr>
      <td width="140">스폰</td>
      <td width="220"><a href="./SpawnSelectionSystem">Spawn&nbsp;/&nbsp;Respawn</a></td>
      <td>Player, AI 공통 요청 흐름, 타입별 스폰 선택, 자동 포인트 등록, 사망 위치 기반 후보 선택, 스폰 사운드</td>
    </tr>
    <tr>
      <td width="140">카메라</td>
      <td width="220"><a href="./DeathCam">DeathCam</a></td>
      <td>Owning Client 전용 카메라, 충돌 보정, Killer Highlight</td>
    </tr>
    <tr>
      <td width="140">게임플레이</td>
      <td width="220"><a href="./Teleport">Portal</a></td>
      <td>서버 권한 이동, 재진입 방지, 클라이언트별 출구 화면, 동적 해상도, Niagara 효과</td>
    </tr>
    <tr>
      <td width="140">캐릭터</td>
      <td width="220"><a href="./CharacterAppearance">Character&nbsp;Appearance</a></td>
      <td>DataTable 기반 메시 선택, 복제, 사망 외형 처리</td>
    </tr>
    <tr>
      <td width="140">캐릭터&nbsp;표시</td>
      <td width="220"><a href="./CharacterDisplay">Character&nbsp;Display</a></td>
      <td>로컬 외곽선, 조준 대상 닉네임, PlayerState 기반 복수 대상 표시</td>
    </tr>
    <tr>
      <td width="140">오디오</td>
      <td width="220"><a href="./Audio">BGM</a></td>
      <td>상태, 맵별 곡 선택, 비동기 로드, Fade 전환, Pause 중 반복 재생</td>
    </tr>
    <tr>
      <td width="140">맵&nbsp;전환</td>
      <td width="220"><a href="./Loading">Loading</a></td>
      <td>서버 준비, 실제 참가자 대기, 로딩 화면과 입력 잠금, BGM 시작 연동</td>
    </tr>
    <tr>
      <td width="140">아이템</td>
      <td width="220"><a href="./DeathDrop">Death&nbsp;Drop</a></td>
      <td>사망 시 드롭 후보 선택, 물리 발사, 착지 복제, 버프 종료 시각 보존</td>
    </tr>
  </tbody>
</table>

## Code Reading Guide

각 폴더는 기능을 빠르게 검토할 수 있도록 핵심 헤더와 구현 파일을 같은 위치에 두었습니다.

- `.h` 파일에서 데이터 구조, 책임 경계, Unreal 반영 지점을 먼저 확인할 수 있습니다.
- `.cpp` 파일에는 실제 흐름과 예외 처리, 네트워크 또는 Editor 전용 분기가 있습니다.
- `JsonAssetSync`, `GameplayValidator`는 원본에서 Unreal Plugin으로 구성했습니다.
- `DataEditor`는 별도 .NET 8 WPF 데스크톱 도구입니다.
- `Blueprints/Weapon`과 `Blueprints/WeaponAimAnimation`에는 구현 노드 검토용 `.uasset` 파일을 기능별로 모았습니다. 각 폴더 바로 아래에서 파일을 확인할 수 있습니다.
- `.uasset`은 바이너리이므로 GitHub 웹에서는 노드 그래프를 직접 볼 수 없습니다. 노드 검토에는 원본 프로젝트와 의존성을 갖춘 Unreal Editor 5.6.1 환경이 필요합니다.

## Blueprint Implementations

직접 기능을 구현한 Blueprint 위주로 14개를 선별했습니다. 기존 GameMode, 캐릭터, UI 등에 기능 호출 노드나 컴포넌트만 연결한 BP는 포함하지 않았습니다. 초기 프로토타입, 테스트/디버그 BP, 인터페이스와 데이터 클래스도 제외했습니다.

| 구분 | 파일 수 | 확인할 내용 |
| --- | --- | --- |
| 무기 공통/개별 BP | 7 | `BP_WeaponBase`와 MachineGun, Pistol, PlasmaGun, RailGun, RocketLauncher, ShotGun의 발사 처리 |
| 투사체 발사 컴포넌트 | 1 | `BPC_ProjectileFire`의 투사체 생성과 발사 흐름 |
| 투사체 BP | 4 | `BP_ProjectileBase`, `BP_PistolProjectile`, `BP_PlasmaProjectile`, `BP_RocketLauncherProjectile`의 공통 처리와 충돌, 폭발, 효과/사운드 연동 |
| 무기 조준 AnimBP | 2 | 플라즈마건과 로켓런처의 조준 방향, 무기 자세 보정 |

### Weapon Blueprint

- [BP_WeaponBase](./Blueprints/Weapon/BP_WeaponBase.uasset)에서 `TryFire`, `StartFire` 등의 공통 발사 흐름과 효과 처리를 확인할 수 있습니다.
- 무기별 BP에는 각 무기에 맞는 발사 처리를 분리했습니다. 투사체를 사용하는 무기는 [BPC_ProjectileFire](./Blueprints/Weapon/BPC_ProjectileFire.uasset)와 투사체 BP를 함께 확인하면 됩니다.
- 투사체 BP에는 생성 이후의 충돌 처리와 폭발, 히트 효과, 사운드 관련 노드가 포함되어 있습니다.

[무기 Blueprint 12개 보기](./Blueprints/Weapon)

### Weapon Aim Animation

- [Steel_ABP_TP_BGH_PlasmaGun_AimAligned](./Blueprints/WeaponAimAnimation/Steel_ABP_TP_BGH_PlasmaGun_AimAligned.uasset)
- [Steel_ABP_TP_BGH_RocketLauncher_AimAligned](./Blueprints/WeaponAimAnimation/Steel_ABP_TP_BGH_RocketLauncher_AimAligned.uasset)

플라즈마건과 로켓런처의 조준 방향에 맞춰 무기 자세를 보정하는 AnimBP입니다. AnimGraph와 조준 관련 갱신 흐름을 확인할 수 있습니다.

줌 BP는 이번 열람 대상에서 제외했고, 현재 사용하지 않는 플레이어 텔레포트 스킬 BP도 포함하지 않았습니다. 아래 `Teleport` 폴더의 레벨 Portal 시스템과는 별개입니다.

Blueprint 파일은 열람하기 쉽도록 원본의 중간 폴더를 생략해 모았습니다. 머티리얼, 메시, 사운드, DataAsset 인스턴스 등의 의존성은 포함하지 않았으며, 그대로 다른 프로젝트의 Content 폴더에 복사해 실행하는 패키지가 아닙니다.

## External Data Pipeline

### JsonAssetSync

외부 JSON을 게임 데이터에 반영하는 Unreal Plugin입니다.

- Runtime에서는 메모리 적용만 수행하고, Editor에서는 Apply 및 에셋 저장을 분리합니다.
- `FJsonAssetSyncSchemaExporter`가 Registry와 Unreal Reflection을 읽어 `JsonAssetSyncManifest.json`을 생성합니다.
- Manifest에는 필드명, 표시명, C++ 타입, Category, 범위 제약, Enum 선택지, 중첩 Struct 정보가 포함됩니다.
- 동기화 대상 에셋을 Editor에서 저장하면 연결된 외부 JSON에도 현재 값을 기록합니다.
- 최신 Manifest에는 캐릭터 모델링 ID와 캐릭터, AI 성격 필드의 설명 및 표시 순서를 반영했습니다.
- Blueprint 필드의 Category는 Reflection에서 얻은 표시명을 유지하며, 현재 Manifest의 Bool, String, Int, Float 항목에는 `디폴트`를 사용합니다.
- 지원 대상은 DataTable, DataAsset, CurveTable, FloatCurve입니다.

[JsonAssetSync 코드 보기](./JsonAssetSync)

### DataEditor

Unreal Editor를 실행하지 않고 프로젝트 데이터를 조회, 수정하기 위한 .NET 8 WPF 도구입니다.

- Manifest와 프로젝트 정보를 읽어 편집 가능한 데이터 대상을 구성합니다.
- DataTable, DataAsset, CurveTable, FloatCurve와 JSON/CSV 흐름을 지원합니다.
- 단일 실행 파일 배포를 고려해 프로젝트 루트와 상대 경로를 검증합니다.

[DataEditor 코드 보기](./DataEditor)

## Level Validation Tool

플레이 테스트 전에 레벨 배치 오류를 검사하는 Unreal Editor Plugin입니다.

- Portal, JumpPad, PathLink를 Provider로 분리해 검사 대상을 확장합니다.
- World Scanner가 Actor를 수집하고, Provider Registry가 검사 로직을 연결합니다.
- Portal은 활성 상태일 때 `Exit Target` 누락, 자기 자신 연결을 검사하고, 공용 Teleport DA의 Launch Angle, Power가 유효한지도 확인합니다.
- JumpPad는 ApexTime / LaunchAngle 유형을 별도 슬롯으로 관리하며, 각 유형의 TargetPoint 누락, 자기 자신 연결, 동일 위치를 보고합니다.
- Reflection 유틸리티로 Blueprint 구현에 직접 의존하지 않고 bool, 숫자, Actor 속성을 읽어 검사합니다.

[GameplayValidator 코드 보기](./GameplayValidator)

## AI Navigation Assist

NavMesh만으로 해결하기 어려운 특수 이동 구간을 AI 경로에 포함하는 시스템입니다.

- `APathLink`가 레벨의 Link Actor 역할을 맡습니다.
- `PathLinkSubsystem`이 등록, Endpoint 검증, Static Graph 생성을 담당합니다.
- `PathLinkRouteFinder`가 Dijkstra로 Shortest Route를 계산합니다.
- Portal, JumpPad, Jump, Drop을 동일한 경로 탐색 흐름으로 처리합니다.

[PathLink 코드 보기](./PathLink)

## Spawn / Respawn

Player와 AI가 공통으로 사용하는 스폰 선택 시스템입니다.

- 서버 BeginPlay에서 레벨의 SpawnPoint와 `Spawn_Type`을 자동 등록합니다.
- `Both`, `PlayerOnly`, `BotOnly`로 후보를 구분하고, Controller에 맞는 전용 포인트를 공용 포인트보다 우선합니다.
- 최초 스폰에서는 배정 수를 기준으로 분산하고, Pawn 생성, Possess 실패 시 배정을 복구합니다.
- 리스폰에서는 선택된 타입의 포인트를 사망 위치에서 먼 순서로 정렬하고, 먼 절반에서 무작위로 선택합니다.
- `CharacterSpawnSoundComponent`가 최초 스폰, 리스폰별 사운드의 지연, 볼륨, 2D/3D 공간, 재생 대상을 제어합니다.
- SpawnPoint 회전은 서버 Controller와 Owning Client에 함께 반영합니다.

[Spawn / Respawn 코드 보기](./SpawnSelectionSystem)

## DeathCam

사망한 Owning Client에만 Killer를 보여주는 카메라 시스템입니다.

- `DeathCamComponent`가 ViewTarget 생명주기와 Owning Client 처리를 담당합니다.
- `DeathCamActor`가 Sweep, Collision Slide, 카메라 위치 보정을 처리합니다.
- Killer가 가려진 경우 Outline/Fill 방식의 Highlight를 적용합니다.

[DeathCam 코드 보기](./DeathCam)

## Portal

서버 권한으로 동작하는 단방향 Portal 시스템입니다.

- `OneWayTeleportActor`가 Overlap, 출구 이동, Controller 회전 동기화, `ForceNetUpdate`를 처리합니다.
- Portal A에서 B로 이동한 직후 발생할 수 있는 Overlap 재진입을 잠가 무한 왕복을 방지합니다.
- `TeleportDataAsset`에서 Launch Angle, Power, Move Lock Time, 사운드 설정을 조정합니다.
- 실제 Portal을 이용한 클라이언트에서만 사운드를 재생합니다.
- `PortalViewSubsystem`이 로컬 카메라를 출구 시점으로 변환해 클라이언트별 SceneCapture와 Render Target을 관리합니다.
- 화면 점유율에 따른 동적 해상도, 단계별, 정지 상태 갱신 빈도, 프레임당 캡처 예산으로 렌더링 비용을 조절합니다.
- Mesh 크기에 맞춘 Collision, 화면, Niagara 효과와 포탈별 VFX 색상, 투명도를 설정합니다.
- Niagara 효과는 크기만 자동 조정하고, 편집한 컴포넌트 위치와 회전은 Construction 재실행과 BeginPlay에서도 유지합니다.

[Portal 코드 보기](./Teleport)

## Character Appearance

캐릭터와 모델링 DataTable을 분리해 일반 메시와 사망 외형을 선택하는 시스템입니다.

- `CharacterAppearanceComponent`가 Character Row에서 Modeling ID를 읽고, Modeling Row를 조회해 메시를 적용합니다.
- `SetModelingRowName` / `SetModelingRowHandle`로 모델링 행을 직접 지정하고, 테이블, 행 유효성을 확인합니다. 기존 Modeling ID 조회도 유지합니다.
- 스폰 시점에 일반 외형과 사망 외형을 동기화합니다.
- 전용 사망 파츠가 있으면 해당 파츠만 교체하고, 없는 파츠는 기존 Ragdoll/Impulse 연출을 유지합니다.
- 에디터 전용 슬롯 메타데이터를 분리해 패키징 빌드에서도 머티리얼 탐색을 안전하게 처리합니다.

[Character Appearance 코드 보기](./CharacterAppearance)

## Character Display

로컬 플레이어에게 다른 캐릭터의 외곽선, 조준 대상 닉네임, 복수 대상을 표시하는 시스템입니다.

- `ShooterOutlineComponent`가 원본 메시를 변경하지 않는 CustomDepth 전용 프록시를 만들고, 카메라별 SceneViewExtension으로 외곽선을 적용합니다.
- 일반 대상과 복수 대상의 Stencil을 분리하고, DeathCam용 값 1은 유지합니다. OwnerOnly 표시, 숨김 파츠, 분할 화면의 카메라별 가시성을 반영하며 DeathCam 중에는 일반 표시를 중지합니다.
- `ShooterNicknameComponent`가 실제 조준점에서 피해 판정용 충돌을 추적하고, 벽 너머나 숨겨진 캐릭터, 사망한 캐릭터를 표시 대상에서 제외합니다. 기존 장착 아이템의 판정 채널과 사거리는 읽기 전용으로 참조합니다.
- `ShooterNicknameWidget`이 기존 HUD를 변경하지 않는 별도 Overlay로 닉네임을 그립니다. 글꼴, 외곽선, 색상, DPI 보정 위치와 조준 이탈 후 유지 시간을 설정할 수 있습니다.
- `ShooterRevengeComponent`가 기존 PlayerState의 `RevengeTarget`을 서버에서 읽고 Owning Client에만 복제합니다. Pawn이 리스폰해도 같은 PlayerState를 기준으로 복수 대상을 식별하며, 기존 킬과 사망 규칙은 변경하지 않습니다.
- `ShooterDisplayLibrary`가 기존 Blueprint의 생존 상태와 표시 이름을 읽으며, 런타임 스타일은 Settings DataAsset에 덮어쓰지 않습니다.
- 원본 `AMyPlayerController` 생성자에서 Outline, Nickname, Revenge 컴포넌트를 연결합니다. 렌더링에는 `r.CustomDepth=3`, Post Process 머티리얼, Settings DataAsset과 `UMG`, `Slate`, `SlateCore`, `RHI`, `RenderCore` 모듈이 필요하며 에셋과 전체 Controller는 이 발췌본에 포함하지 않았습니다.
- `CharacterDisplayTests.cpp`, `RevengePlayerStateBridgeTests.cpp`에 충돌 판정, 가시성, 프록시 정리, 복수 대상 동기화 관련 Unreal Automation 테스트가 포함됩니다. 테스트 실행에는 원본 프로젝트와 에셋이 필요합니다.

[Character Display 코드 보기](./CharacterDisplay)

## BGM

레벨 전환을 넘어 유지되는 클라이언트 전용 배경 음악 시스템입니다.

- `BGMSubsystem`이 메인 메뉴, 로비, 게임플레이, 결과 상태와 맵별 트랙을 선택하고 Fade로 전환합니다.
- Soft Reference 비동기 로드에 요청 ID를 사용해 오래된 요청이 최신 재생 상태를 덮어쓰지 않도록 합니다.
- `BGMPlayback`이 원본 SoundWave/SoundCue를 변경하지 않고 임시 Cue로 오디오 스레드 반복 재생을 구성해 Pause 중에도 반복을 유지합니다.
- MetaSound 등 절차형 사운드는 자체 그래프의 Loop 설정이 필요합니다.
- 게임플레이 맵 전환 시 기존 곡을 종료하고, LoadingCoordinator의 준비 완료 신호에서 게임 BGM을 시작합니다.
- `BGMPlaybackTests.cpp`에는 반복 재생과 원본 에셋 보존 관련 Unreal Automation 테스트가 포함됩니다.

[BGM 코드 보기](./Audio)

## Loading

서버의 준비 상태와 클라이언트의 맵 전환 화면을 연결하는 로딩 시스템입니다.

- `LoadingCoordinatorWorldSubsystem`이 서버 월드에 Coordinator를 자동 생성합니다.
- `LoadingCoordinator`가 서버 초기화와 예상 실제 참가자 수를 확인해 준비 단계를 복제합니다.
- `LoadingScreenSubsystem`이 Slate 화면을 표시하고 입력을 잠근 뒤 준비 상태에 따라 해제합니다.
- `LoadingBlueprintLibrary`가 로딩 화면 표시 후 맵 열기, 메인 메뉴 복귀 등의 Blueprint 진입점을 제공합니다.
- `EndLoadingScreen`에서 `EndExternalLoading`을 호출해 서버 대기 실패나 타임아웃 시 외부 로딩 화면과 입력 잠금을 명시적으로 해제할 수 있습니다.
- 메인 메뉴 복귀 시 로컬 서버 종료는 원본 GameInstance의 `HandlePreLoadMap`에서 처리합니다. 화면 표시 콜백에서 종료하지 않아 새로 생성된 서버를 잘못 중지하는 상황을 피합니다.
- GameMode의 `MarkServerWorldReady` 호출과 `ExpectedHumanPlayers` URL 옵션으로 준비 조건을 연결하며, 기존 흐름을 위한 fallback도 포함합니다.

[Loading 코드 보기](./Loading)

## Death Drop

서버에서 사망 시점의 아이템 후보를 수집하고 물리 드롭을 생성하는 시스템입니다.

- `DeathDropSubsystem`이 체력 변경 직전 호출되어 중복 처리를 막고, 설정된 최대 개수만큼 후보를 중복 없이 무작위로 선택합니다.
- `DeathDropCarrier`가 방사 방향 발사, 수평 감속, 착지, 픽업 연결과 상태 복제를 담당합니다.
- 활성 버프는 기존 서버 종료 시각을 유지하며, 미획득 드롭은 설정된 시간 후 제거합니다.
- `DeathDropSettings`에서 후보 종류, 발사 범위, 감속, 수명, 픽업 데이터를 조정합니다.
- 원본의 초기 로드아웃, 픽업 Blueprint에 의존하는 발췌 코드입니다.

[Death Drop 코드 보기](./DeathDrop)
