# Portfolio Code

Unreal Engine 팀 프로젝트에서 직접 작업한 주요 시스템의 소스 코드를
포트폴리오 열람용으로 정리한 저장소입니다.

원본 팀 프로젝트에서는 Unreal 모듈 규칙과 협업을 위해 Public/Private 및 기능별 폴더로
분리되어 있었지만, 여기서는 코드를 빠르게 확인할 수 있도록 시스템 단위로 단순화했습니다.

본 저장소는 코드 열람을 위한 포트폴리오 저장소이며,
원본 프로젝트의 디렉터리 구조를 단순화했기 때문에 독립 빌드를 목적으로 하지 않습니다.

## PathLink

AI가 Teleport / JumpPad / Jump / Drop을 포함한 특수 이동 경로를 탐색할 수 있도록 구현한 시스템입니다.

- `PathLink.cpp / .h` : 레벨에 배치되는 Link Actor
- `PathLinkSubsystem.cpp / .h` : Link 등록, 검증, Static Graph 관리
- `PathLinkRouteFinder.cpp / .h` : Dijkstra 기반 Route 탐색

[코드 보기](./PathLink)

## DeathCam

사망 시 Killer를 보여주는 카메라 시스템입니다.

- `DeathCamActor.cpp / .h` : Camera Collision, Collision Slide, Killer Highlight
- `DeathCamComponent.cpp / .h` : Owning Client, ViewTarget 생명주기 관리
- `DeathCamDataAsset.h` : DeathCam 설정 데이터

[코드 보기](./DeathCam)

## Spawn / Respawn

Player와 AI가 공통으로 사용하는 스폰 선택 시스템입니다.

- `SpawnSelectionComponent.cpp / .h` : 최초 스폰 분산, 사망 위치 기반 리스폰, Spawn / Possess 처리
- SpawnPoint Transform의 회전을 서버 Controller와 Owning Client에 함께 적용

[코드 보기](./SpawnSelectionSystem)

## Portal / Teleport

서버 권한으로 동작하는 단방향 포탈 시스템입니다.

- `OneWayTeleportActor.cpp / .h` : Overlap 처리, 출구 이동, 회전 동기화, 입력 잠금
- `TeleportDataAsset.h` : Launch Angle / Power / Move Lock Time 설정

[코드 보기](./Teleport)

## JsonAssetSync

외부 JSON 데이터를 Unreal Editor와 Runtime에서 적용하기 위한 시스템입니다.

- `JsonApplyService.cpp / .h` : 외부 데이터 적용 처리
- `JsonAssetSyncSubsystem.cpp / .h` : 적용 진입점과 상태 관리
- `JsonAssetSyncManifest.json` : 동기화 대상과 외부 JSON 소스 메타데이터

원본 프로젝트에서는 Unreal Plugin 형태로 구성되어 있었습니다.

[코드 보기](./JsonAssetSync)

## GameplayValidator

Portal / JumpPad / PathLink의 잘못된 레벨 배치를
플레이 테스트 전에 검사하는 Editor Validation Tool입니다.

- `PortalValidationProvider.*`
- `JumpPadValidationProvider.*`
- `PathLinkValidationProvider.*`
- `GameplayValidationProviderRegistry.*`
- `GameplayValidationWorldScanner.*`
- `ShootingArenaModule.cpp` : 프로젝트의 실제 `APathLink` API를 Validator Native Bridge에 연결

원본 프로젝트에서는 Unreal Editor Plugin 형태로 구성되어 있었습니다.

[코드 보기](./GameplayValidator)

## DataEditor

Unreal Editor를 실행하지 않고 외부 데이터를 조회·수정하기 위한 데스크톱 편집 도구입니다.

- `MainWindow.xaml / .cs`
- `Core/`
- `Editors/`
- `JsonAssetDataEditor.csproj`

[코드 보기](./DataEditor)
