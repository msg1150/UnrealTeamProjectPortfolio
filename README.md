# BattleArena - Game Programming Portfolio

BattleArena 멀티플레이 슈팅 팀 프로젝트에서 직접 구현한 게임플레이 시스템과 제작 도구를 정리한 코드 열람용 저장소입니다.

- 개발 기간: 2025.12 ~ 현재 진행 중
- 개발 환경: Unreal Engine 5.6.1, C++, Blueprint, .NET 8 WPF
- [Notion 기술 문서](https://pjhgameprogramming.notion.site/Game-Programming-Technical-Wiki-3cea186e6315804cb41ff052b738453a): 시스템 설계, 알고리즘, 문제 해결 과정과 원본 프로젝트 연결 방법

> C++ 코드와 선별한 Blueprint를 모은 발췌본입니다. 전체 에셋과 모듈 의존성을 포함하지 않아 독립 빌드나 실행을 지원하지 않습니다.

## 핵심 구현과 코드 위치

| 영역 | 코드 | 확인할 내용 |
| --- | --- | --- |
| 무기와 투사체 | [Weapon](./Weapon) | 조준 방향 보정, 투사체 경로 수렴, 충돌 분리, 1인칭 렌더링과 레일건/머신건 빔 보정 |
| 객체 수명 관리 | [CharacterPool](./CharacterPool) / [Projectile Pool](./Weapon/ProjectilePoolSubsystem.cpp) | 참가자별 Pawn 재사용, 재접속 예약, 월드별 투사체 풀과 다음 생명 상태 초기화 |
| 무기 Blueprint | [Weapon Blueprint](./Blueprints/Weapon) | 공통 발사 흐름, 무기별 발사 처리, 투사체 생성, 피해와 폭발 연출 연결 |
| 조준 애니메이션 | [Weapon Aim Animation](./Blueprints/WeaponAimAnimation) | 플라즈마건과 로켓런처의 조준 방향에 맞춘 무기 자세 보정 |
| AI 경로 탐색 | [PathLink](./PathLink) | 포탈, 점프패드, 점프, 낙하를 포함하는 경로와 Dijkstra 탐색 |
| 포탈 | [Seamless Portal](./Teleport) | 공간 변환, owner-only 도착 동기화, 캡처 이력/컷 처리와 안전한 도착 검사 |
| 조준점 반동 | [Crosshair](./Crosshair) | 원본 UMG 애니메이션을 보존하는 인스턴스별 반동 간격 보정 |
| 외부 데이터 동기화 | [JsonAssetSync](./JsonAssetSync) | Reflection 기반 명세, Cook된 프로퍼티 이름 정규화, Registry 저장과 패키징 검증 |
| 데이터 편집 도구 | [DataEditor](./DataEditor) | Unreal Editor 없이 테이블, 에셋, 커브를 편집하는 .NET 8 WPF 도구 |
| 레벨 검증 도구 | [GameplayValidator](./GameplayValidator) | 새 공간 포탈 검사, 점프패드 연결 검사와 Editor 수정 동작 |

## 주요 문제 해결

### 투사체와 캐릭터 재사용 시 이전 상태 분리

투사체는 월드별 Plasma/Rocket 풀에서 재사용하고, 사전 생성 예산과 보관 용량을 Project Settings로 조절합니다. 용량을 넘는 발사는 임시 Actor로 처리해 풀 크기가 발사를 막지 않도록 했습니다. 무기와 발사 ID로 시각용 복사본을 추적하며, 캐릭터의 다음 생명과 이전 투사체의 Instigator가 섞이지 않도록 분리합니다.

캐릭터는 참가자별 Pawn을 재사용하고 재접속 예약을 별도로 관리합니다. 생명 상태, 인벤토리/버프, 물리와 애니메이션, AI와 타이머를 다음 생명에 맞게 초기화합니다. 공유 Controller와 AI 클래스에 추가한 연결 코드는 전체 팀 코드를 복사하지 않고 변경분으로 발췌했습니다.

[투사체 풀](./Weapon/ProjectilePoolSubsystem.cpp) / [캐릭터 풀](./CharacterPool/CharacterPoolSubsystem.cpp) / [공유 코드 연결 변경](./Integration/PJH_RuntimeHooks.patch)

### 조준점과 실제 투사체 방향 맞추기

카메라와 총구 위치가 달라 근거리에서 조준점과 투사체 방향이 어긋나는 문제를 처리했습니다. 총구에서 조준선의 합류 지점까지 이동한 뒤 전방 비행으로 전환하며, 한 프레임 안에 합류 지점을 지나면 이동 시간을 나눠 계산합니다. 이동 중 충돌로 정지하거나 소멸하면 후속 이동을 중단합니다.

[투사체 이동 코드](./Weapon/ProjectileConvergenceComponent.cpp) / [조준 좌표와 안전한 발사 위치](./Weapon/WeaponProjectileAimLibrary.cpp)

### 레일건과 머신건 빔의 1인칭 표시 보정

로컬 1인칭 총구가 월드 지형에 가려진 경우에만 준비된 Niagara/머티리얼 변형을 선택합니다. 물리 총구 좌표와 실제 명중 끝점은 유지하고 가까운 빔 형상만 1인칭 공간으로 보정해 이중 변환을 피합니다. 짧은 레일건 빔에서는 물결 구간, 폭과 흔들림을 길이에 맞춰 조절합니다. 피해 판정과 원격 시점은 변경하지 않습니다.

[빔 효과 코드](./Weapon/WeaponBeamEffectLibrary.cpp) / [근거리 물결 계산](./Weapon/RailBeamWaveMath.h)

### 새 공간 포탈의 화면과 이동 연결

`SeamlessPortalActor`가 입구/출구 좌표계로 위치, 속도와 시점을 변환하고 비대칭 투영으로 출구를 창처럼 표시합니다. 서버는 캐릭터의 실제 이동 구간과 포탈 입구를 검사하고, 도착 Capsule 충돌과 경로를 확인한 뒤 이동합니다. 기본 가로 중앙 도착 옵션과 재진입 잠금을 지원합니다. 기존 `OneWayTeleportActor`, `PortalViewSubsystem`, `TeleportDataAsset`은 새 포탈이 상속하거나 재사용하는 기반이므로 유지했습니다.

[새 포탈](./Teleport/SeamlessPortalActor.cpp) / [공간 변환과 투영](./Teleport/SeamlessPortalMath.cpp) / [통과 관리](./Teleport/SeamlessPortalTraversalSubsystem.cpp)

서버가 승인한 도착 위치, 속도와 시점은 `SeamlessPortalSyncComponent`의 owner-only Client RPC로 전달합니다. 로컬의 이전 이동 예측과 스무딩 상태를 정리하되 클라이언트에 통과 권한을 주지는 않습니다. 캡처의 카메라 컷과 갱신 이력도 관리해 포탈 이동이나 시점 불연속을 처리합니다.

### 벽 뒤의 적이 투사체에 맞는 문제 해결

플레이어 명중 검사와 환경 충돌 검사의 반경을 분리하고, 투사체가 실제로 지나간 구간에서 먼저 닿은 충돌을 선택합니다. 명중 검사 범위가 얇은 벽 뒤의 적까지 닿는 경우에는 추가 Line Trace로 가려진 대상을 제외합니다. 피해 판정은 서버가 맡으며, 서버 충돌 알림으로 같은 무기와 발사 ID의 시각용 투사체만 제거합니다.

[충돌 판정 코드](./Weapon/ProjectileWorldCollisionComponent.cpp) / [Blueprint 연결 도구](./Weapon/Editor)

### 사망 드롭의 소멸 전 축소

미획득 드롭은 수명이 끝나기 직전에 메시가 서서히 작아집니다. `DespawnShrinkEnabled`와 `DespawnShrinkDuration`으로 사용 여부와 시간을 설정하며, 예시 설정은 수명 3초 중 마지막 0.1초에 축소합니다. 축소 시작과 제거 시각을 서버 시간으로 복제해 늦게 표시된 클라이언트도 현재 진행률로 합류합니다.

기존 BP의 회전/상하 애니메이션과 픽업 충돌은 유지하도록 메시 위에 임시 시각용 부모를 추가해 스케일을 조절합니다. 아이템을 획득하면 축소를 복구하며, 수명 만료로 제거될 때 원래 크기가 잠깐 다시 보이지 않게 처리합니다. 적용할 수 없는 메시에는 기존 즉시 제거 방식이 유지됩니다.

[드롭 소멸 처리](./DeathDrop/DeathDropCarrier.cpp) / [드롭 설정](./DeathDrop/DeathDropSettings.h) / [설정 예시](./JsonAssetSync/Examples/DataTables/DT_DropItemSetting.json)

### AI 특수 이동과 반복 계산 줄이기

NavMesh의 일반 이동에 포탈, 점프패드, 점프, 낙하 연결을 추가하고 이동 비용을 함께 비교합니다. 특수 이동 지점 사이의 거리와 연결 정보를 정적 그래프에 캐시하며, 요청별 출발점과 도착점 후보를 검증한 뒤 최종 선택한 일반 이동 구간만 상세 경로 좌표를 생성합니다.

[경로 탐색 코드](./PathLink/PathLinkRouteFinder.cpp) / [그래프 캐시 관리](./PathLink/PathLinkSubsystem.cpp)

### 외부 데이터 편집과 안전한 반영

Unreal Reflection에서 타입, 입력 범위, Enum과 중첩 구조를 추출해 데이터 명세를 생성하고 WPF 편집기가 이를 읽습니다. 저장 전에 입력값을 검증하며, Runtime의 메모리 적용과 Editor의 에셋 저장을 분리합니다. 동기화 대상 에셋을 Editor에서 저장하면 연결된 JSON에도 현재 값을 기록합니다.

Cook 전후의 프로퍼티 이름 차이는 Reflection 기반 정규화로 처리하며, 알 수 없거나 모호한 필드를 조용히 버리지 않습니다. Registry의 현재 값을 JSON으로 저장하는 흐름과 Cook/Stage 사전 검증을 보완했습니다. [데이터 예시](./JsonAssetSync/Examples)는 PJH 브랜치에 커밋된 JSON 발췌본이며, 전체 게임 데이터나 팀원 데이터 구조의 개인 구현을 의미하지 않습니다.

[데이터 명세 생성](./JsonAssetSync/JsonAssetSyncSchemaExporter.cpp) / [JSON 적용 서비스](./JsonAssetSync/JsonApplyService.cpp) / [WPF 편집기](./DataEditor)

## 추가 구현

| 시스템 | 코드 | 확인할 내용 |
| --- | --- | --- |
| 스폰과 리스폰 | [SpawnSelectionSystem](./SpawnSelectionSystem) | 스폰 지점 선택, 최초 접속/로딩/카메라 준비 후 사운드 재생과 중복 방지 |
| 사망 카메라 | [DeathCam](./DeathCam) | 좁은 모서리의 사망 위치 시점, Sweep/Slide 충돌 보정, 킬러 하이라이트 |
| 캐릭터 표시 | [CharacterDisplay](./CharacterDisplay) | 카메라별 외곽선, 조준 대상 닉네임, PlayerState 기반 복수 대상 표시 |
| 캐릭터 외형 | [CharacterAppearance](./CharacterAppearance) | DataTable 기반 메시 선택, 외형 복제와 사망 파츠 처리 |
| 사망 아이템 드롭 | [DeathDrop](./DeathDrop) | 드롭 후보 선택, 착지 복제, 생명별 중복 처리 분리와 서버 시간 기반 소멸 전 축소 |
| 로딩 | [Loading](./Loading) | 서버 준비와 실제 참가자 대기, 소유 GameInstance별 전환 처리와 BGM 볼륨 연동 |
| 배경 음악 | [Audio](./Audio) | 메뉴/게임플레이 곡 선택, 로딩 중 감쇠와 복구, 같은 곡의 재생 위치 유지 |

## 코드 열람 안내

- 각 시스템의 `.h`에서 데이터 구조와 책임을, `.cpp`에서 실제 처리와 예외 대응을 확인할 수 있습니다. 원본의 Public/Private 구조는 열람하기 쉽도록 기능별로 모았습니다.
- `Weapon/Editor`는 Blueprint 연결과 Custom AnimGraph 노드를 위한 Editor 전용 코드입니다. `JsonAssetSync`와 `GameplayValidator`는 Unreal 플러그인, `DataEditor`는 별도 WPF 도구입니다.
- `Integration/ShootingArena.Build.cs`는 원본 모듈 연결 참고 자료입니다. 조준점의 `MovieScene`과 새 포탈의 `MeshDescription`, `StaticMeshDescription` 의존성을 확인할 수 있으며, 모듈 전체를 개인 구현으로 의미하지 않습니다.
- `Integration/PJH_RuntimeHooks.patch`와 [연결 참고](./Integration/README.md)는 공유 코드의 개인 변경 부분만 설명합니다. `PackageAll.bat`는 원본 프로젝트 루트에서 사용하는 패키징 스크립트의 참고 사본입니다.
- Blueprint는 무기 공통/개별 BP 7개, 발사 컴포넌트 1개, 투사체 BP 4개, 조준 AnimBP 2개로 총 14개입니다. 직접 구현한 기능 위주로 선별했습니다.
- `.uasset`의 노드 그래프는 GitHub 웹에서 볼 수 없습니다. 원본 프로젝트와 의존성을 갖춘 Unreal Editor 5.6.1 환경에서 열람해야 합니다.

## 테스트 코드와 기준 버전

Unreal Automation 테스트 코드를 함께 수록했습니다. 대표 확인 항목은 다음과 같습니다.

- [투사체 수렴](./Weapon/ProjectileConvergenceTests.cpp): 한 프레임 안의 합류 지점 통과, 남은 시간의 이동과 충돌 정지
- [충돌 분리](./Weapon/ProjectileSplitCollisionTests.cpp): 벽과 플레이어의 충돌 순서, 얇은 벽 뒤 명중 차단, 이동 구간과 컴포넌트 생명주기
- [1인칭 렌더링](./Weapon/FirstPersonRenderingTests.cpp): 캐릭터 충돌 영역 안의 스케일 계산, FOV와 Near Clip 입력 안전성
- [닉네임 보조 검색](./CharacterDisplay/ShooterNicknameSphereTests.cpp): 정확한 조준 대상 우선, 보조 검색 설정, 벽 가림과 잘못된 입력
- [공간 포탈](./Teleport/SeamlessPortalTests.cpp): 좌표/속도 변환, 비대칭 투영, 통과 조건, 도착 충돌과 기존 포탈 분리
- [최초 스폰 사운드](./SpawnSelectionSystem/CharacterSpawnSoundTests.cpp): 접속 준비, 로딩/카메라 대기, 중복 재생과 지연 사운드 수명
- [캐릭터 재사용 초기화](./CharacterPool/CharacterPoolResetTests.cpp): 생명 전환과 이전 상태 정리
- [투사체 풀 설정](./Weapon/ProjectilePoolSettingsTests.cpp): 월드 설정과 사전 생성/보관 용량 검증

## 동기화 범위

2026-11-02 기준 [BattleArena main 2c25acc37](https://github.com/Qusbell/BattleArena/commit/2c25acc374ce2947d99a4ce841ef88438ac9ef41)에서 병합된 PJH 작업을 선별하고, 해당 파일은 현재 main 버전으로 반영했습니다. DropItem의 소멸 전 축소와 JSON 설정 예시도 포함합니다. 별도 브랜치에만 남은 작업과 수정 중인 로컬 파일은 제외했습니다. 이번 갱신에서 빌드와 실행 검증은 수행하지 않았습니다.

`PathLink/`는 기존 개인 구현 버전으로 고정했습니다. 외곽선과 닉네임의 `CharacterDisplay/` 및 관련 설정/Manifest 항목도 이번 갱신에서 보류했습니다. [동기화 정책](./portfolio-sync-policy.json)을 따르며, 머티리얼, 메시, 사운드 등 의존 에셋과 줌/미사용 플레이어 텔레포트 스킬 BP는 기존 열람 범위에서 제외합니다.
