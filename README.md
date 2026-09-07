# Unreal Team Project Portfolio

Unreal Engine 멀티플레이 팀 프로젝트에서 구현한 게임플레이 시스템과 제작 도구를 포트폴리오 열람용으로 정리한 저장소입니다.

프로젝트는 Unreal Engine 5.6.1, C++, Blueprint, .NET 8을 사용했습니다. 아래 코드는 원본 프로젝트의 Public/Private 구조와 의존성을 시스템 단위로 재구성한 발췌본입니다.

> 이 저장소는 코드 리뷰용 포트폴리오입니다. 원본 프로젝트의 에셋, 플러그인 설정, Blueprint, 모듈 의존성이 모두 포함되어 있지 않아 독립 빌드를 지원하지 않습니다.

## System Map

| 영역 | 시스템 | 확인할 내용 |
| --- | --- | --- |
| 데이터 | [JsonAssetSync](./JsonAssetSync) | Reflection 기반 Manifest/Schema Export, Editor·Runtime 적용 분리, 에셋 저장 시 JSON 기록 |
| 도구 | [DataEditor](./DataEditor) | Manifest를 읽는 .NET 8 데이터 편집 도구, JSON/CSV 편집 |
| 도구 | [Validator](./GameplayValidator) | Portal·JumpPad·PathLink의 레벨 배치 오류를 Provider 구조로 검사 |
| AI | [PathLink](./PathLink) | Portal·JumpPad·Jump·Drop을 포함하는 Static Graph와 Dijkstra 경로 탐색 |
| 스폰 | [Spawn](./SpawnSelectionSystem) | Player·AI 공통 요청 흐름, 최초 스폰 분산과 사망 위치 기반 후보 선택 |
| 카메라 | [DeathCam](./DeathCam) | Owning Client 전용 카메라, 충돌 보정, Killer Highlight |
| 게임 | [Portal](./Teleport) | 서버 권한 이동, 재진입 방지, Launch·입력 잠금, 이용자 전용 사운드 |
| 캐릭터 | [Appearance](./CharacterAppearance) | DataTable 기반 메시 선택, 복제, 사망 외형 처리 |

## Code Reading Guide

각 폴더는 기능을 빠르게 검토할 수 있도록 핵심 헤더와 구현 파일을 같은 위치에 두었습니다.

- `.h` 파일에서 데이터 구조, 책임 경계, Unreal 반영 지점을 먼저 확인할 수 있습니다.
- `.cpp` 파일에는 실제 흐름과 예외 처리, 네트워크 또는 Editor 전용 분기가 있습니다.
- `JsonAssetSync`, `GameplayValidator`는 원본에서 Unreal Plugin으로 구성했습니다.
- `DataEditor`는 별도 .NET 8 WPF 데스크톱 도구입니다.

## External Data Pipeline

### JsonAssetSync

외부 JSON을 게임 데이터에 반영하는 Unreal Plugin입니다.

- Runtime에서는 메모리 적용만 수행하고, Editor에서는 Apply 및 에셋 저장을 분리합니다.
- `FJsonAssetSyncSchemaExporter`가 Registry와 Unreal Reflection을 읽어 `JsonAssetSyncManifest.json`을 생성합니다.
- Manifest에는 필드명, 표시명, C++ 타입, Category, 범위 제약, Enum 선택지, 중첩 Struct 정보가 포함됩니다.
- 동기화 대상 에셋을 Editor에서 저장하면 연결된 외부 JSON에도 현재 값을 기록합니다.
- 지원 대상은 DataTable, DataAsset, CurveTable, FloatCurve입니다.

[JsonAssetSync 코드 보기](./JsonAssetSync)

### DataEditor

Unreal Editor를 실행하지 않고 프로젝트 데이터를 조회·수정하기 위한 .NET 8 WPF 도구입니다.

- Manifest와 프로젝트 정보를 읽어 편집 가능한 데이터 대상을 구성합니다.
- DataTable, DataAsset, CurveTable, FloatCurve와 JSON/CSV 흐름을 지원합니다.
- 단일 실행 파일 배포를 고려해 프로젝트 루트와 상대 경로를 검증합니다.

[DataEditor 코드 보기](./DataEditor)

## Level Validation Tool

플레이 테스트 전에 레벨 배치 오류를 검사하는 Unreal Editor Plugin입니다.

- Portal, JumpPad, PathLink를 Provider로 분리해 검사 대상을 확장합니다.
- World Scanner가 Actor를 수집하고, Provider Registry가 검사 로직을 연결합니다.
- Portal의 대상 누락·Self Target, JumpPad의 TargetPoint 누락·잘못된 연결 등을 보고합니다.

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

- 최초 스폰에서는 사용 가능한 SpawnPoint에 캐릭터를 분산합니다.
- 리스폰에서는 사망 위치와 SpawnPoint의 거리를 기준으로 후보를 구성합니다.
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

[Portal 코드 보기](./Teleport)

## Character Appearance

캐릭터와 모델링 DataTable을 분리해 일반 메시와 사망 외형을 선택하는 시스템입니다.

- `CharacterAppearanceComponent`가 Character Row에서 Modeling ID를 읽고, Modeling Row를 조회해 메시를 적용합니다.
- 스폰 시점에 일반 외형과 사망 외형을 동기화합니다.
- 전용 사망 파츠가 있으면 해당 파츠만 교체하고, 없는 파츠는 기존 Ragdoll/Impulse 연출을 유지합니다.
- 에디터 전용 슬롯 메타데이터를 분리해 패키징 빌드에서도 머티리얼 탐색을 안전하게 처리합니다.

[Character Appearance 코드 보기](./CharacterAppearance)
