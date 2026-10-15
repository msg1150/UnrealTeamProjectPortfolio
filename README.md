# BattleArena - Game Programming Portfolio

BattleArena 멀티플레이 슈팅 팀 프로젝트에서 직접 구현한 게임플레이 시스템과 제작 도구를 정리한 코드 열람용 저장소입니다.

- 개발 기간: 2025.12 ~ 현재 진행 중
- 개발 환경: Unreal Engine 5.6.1, C++, Blueprint, .NET 8 WPF
- [Notion 기술 문서](https://pjhgameprogramming.notion.site/Game-Programming-Technical-Wiki-3cea186e6315804cb41ff052b738453a): 시스템 설계, 알고리즘, 문제 해결 과정과 원본 프로젝트 연결 방법

> C++ 코드와 선별한 Blueprint를 모은 발췌본입니다. 전체 에셋과 모듈 의존성을 포함하지 않아 독립 빌드나 실행을 지원하지 않습니다.

## 핵심 구현과 코드 위치

| 영역 | 코드 | 확인할 내용 |
| --- | --- | --- |
| 무기와 투사체 | [Weapon](./Weapon) | 조준 방향 보정, 투사체 경로 수렴, 플레이어와 환경의 충돌 분리, 1인칭 렌더링 |
| 무기 Blueprint | [Weapon Blueprint](./Blueprints/Weapon) | 공통 발사 흐름, 무기별 발사 처리, 투사체 생성, 피해와 폭발 연출 연결 |
| 조준 애니메이션 | [Weapon Aim Animation](./Blueprints/WeaponAimAnimation) | 플라즈마건과 로켓런처의 조준 방향에 맞춘 무기 자세 보정 |
| AI 경로 탐색 | [PathLink](./PathLink) | 포탈, 점프패드, 점프, 낙하를 포함하는 경로와 Dijkstra 탐색 |
| 포탈 | [Portal](./Teleport) | 서버 권한 이동, 재진입 방지, 플레이어별 출구 화면과 캡처 비용 조절 |
| 외부 데이터 동기화 | [JsonAssetSync](./JsonAssetSync) | Reflection 기반 데이터 명세 생성, JSON 적용과 에셋 저장 분리 |
| 데이터 편집 도구 | [DataEditor](./DataEditor) | Unreal Editor 없이 테이블, 에셋, 커브를 편집하는 .NET 8 WPF 도구 |
| 레벨 검증 도구 | [GameplayValidator](./GameplayValidator) | 포탈, 점프패드, 특수 이동 연결의 배치 오류 검사 |

## 주요 문제 해결

### 조준점과 실제 투사체 방향 맞추기

카메라와 총구 위치가 달라 근거리에서 조준점과 투사체 방향이 어긋나는 문제를 처리했습니다. 총구에서 조준선의 합류 지점까지 이동한 뒤 전방 비행으로 전환하며, 한 프레임 안에 합류 지점을 지나면 이동 시간을 나눠 계산합니다. 이동 중 충돌로 정지하거나 소멸하면 후속 이동을 중단합니다.

[투사체 이동 코드](./Weapon/ProjectileConvergenceComponent.cpp) / [조준 좌표와 안전한 발사 위치](./Weapon/WeaponProjectileAimLibrary.cpp)

### 벽 뒤의 적이 투사체에 맞는 문제 해결

플레이어 명중 검사와 환경 충돌 검사의 반경을 분리하고, 투사체가 실제로 지나간 구간에서 먼저 닿은 충돌을 선택합니다. 명중 검사 범위가 얇은 벽 뒤의 적까지 닿는 경우에는 추가 Line Trace로 가려진 대상을 제외합니다. 피해 판정은 서버가 맡으며, 서버 충돌 알림으로 같은 무기와 발사 ID의 시각용 투사체만 제거합니다.

[충돌 판정 코드](./Weapon/ProjectileWorldCollisionComponent.cpp) / [Blueprint 연결 도구](./Weapon/Editor)

### AI 특수 이동과 반복 계산 줄이기

NavMesh의 일반 이동에 포탈, 점프패드, 점프, 낙하 연결을 추가하고 이동 비용을 함께 비교합니다. 특수 이동 지점 사이의 거리와 연결 정보를 정적 그래프에 캐시하며, 요청별 출발점과 도착점 후보를 검증한 뒤 최종 선택한 일반 이동 구간만 상세 경로 좌표를 생성합니다.

[경로 탐색 코드](./PathLink/PathLinkRouteFinder.cpp) / [그래프 캐시 관리](./PathLink/PathLinkSubsystem.cpp)

### 외부 데이터 편집과 안전한 반영

Unreal Reflection에서 타입, 입력 범위, Enum과 중첩 구조를 추출해 데이터 명세를 생성하고 WPF 편집기가 이를 읽습니다. 저장 전에 입력값을 검증하며, Runtime의 메모리 적용과 Editor의 에셋 저장을 분리합니다. 동기화 대상 에셋을 Editor에서 저장하면 연결된 JSON에도 현재 값을 기록합니다.

[데이터 명세 생성](./JsonAssetSync/JsonAssetSyncSchemaExporter.cpp) / [JSON 적용 서비스](./JsonAssetSync/JsonApplyService.cpp) / [WPF 편집기](./DataEditor)

## 추가 구현

| 시스템 | 코드 | 확인할 내용 |
| --- | --- | --- |
| 스폰과 리스폰 | [SpawnSelectionSystem](./SpawnSelectionSystem) | 플레이어와 AI의 스폰 지점 선택, 생성 실패 시 배정 복구, 사망 위치에서 먼 후보 선택 |
| 사망 카메라 | [DeathCam](./DeathCam) | 사망한 플레이어의 카메라 전환, 지형 충돌 보정, 킬러 하이라이트 |
| 캐릭터 표시 | [CharacterDisplay](./CharacterDisplay) | 카메라별 외곽선, 조준 대상 닉네임, PlayerState 기반 복수 대상 표시 |
| 캐릭터 외형 | [CharacterAppearance](./CharacterAppearance) | DataTable 기반 메시 선택, 외형 복제와 사망 파츠 처리 |
| 사망 아이템 드롭 | [DeathDrop](./DeathDrop) | 드롭 후보 선택, 물리 발사, 착지 위치 복제와 버프 종료 시각 보존 |
| 로딩 | [Loading](./Loading) | 서버 준비와 실제 참가자 대기, 로딩 화면과 입력 잠금, BGM 시작 연동 |
| 배경 음악 | [Audio](./Audio) | 상태별 곡 선택, 비동기 로드, Fade 전환과 Pause 중 반복 재생 |

## 코드 열람 안내

- 각 시스템의 `.h`에서 데이터 구조와 책임을, `.cpp`에서 실제 처리와 예외 대응을 확인할 수 있습니다. 원본의 Public/Private 구조는 열람하기 쉽도록 기능별로 모았습니다.
- `Weapon/Editor`는 Blueprint 연결과 Custom AnimGraph 노드를 위한 Editor 전용 코드입니다. `JsonAssetSync`와 `GameplayValidator`는 Unreal 플러그인, `DataEditor`는 별도 WPF 도구입니다.
- Blueprint는 무기 공통/개별 BP 7개, 발사 컴포넌트 1개, 투사체 BP 4개, 조준 AnimBP 2개로 총 14개입니다. 직접 구현한 기능 위주로 선별했습니다.
- `.uasset`의 노드 그래프는 GitHub 웹에서 볼 수 없습니다. 원본 프로젝트와 의존성을 갖춘 Unreal Editor 5.6.1 환경에서 열람해야 합니다.

## 테스트 코드와 기준 버전

Unreal Automation 테스트 코드를 함께 수록했습니다. 대표 확인 항목은 다음과 같습니다.

- [투사체 수렴](./Weapon/ProjectileConvergenceTests.cpp): 한 프레임 안의 합류 지점 통과, 남은 시간의 이동과 충돌 정지
- [충돌 분리](./Weapon/ProjectileSplitCollisionTests.cpp): 벽과 플레이어의 충돌 순서, 얇은 벽 뒤 명중 차단, 이동 구간과 컴포넌트 생명주기
- [1인칭 렌더링](./Weapon/FirstPersonRenderingTests.cpp): 캐릭터 충돌 영역 안의 스케일 계산, FOV와 Near Clip 입력 안전성
- [닉네임 보조 검색](./CharacterDisplay/ShooterNicknameSphereTests.cpp): 정확한 조준 대상 우선, 보조 검색 설정, 벽 가림과 잘못된 입력
