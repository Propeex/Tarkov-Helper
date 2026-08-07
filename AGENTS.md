# Tarkov-Helper Agent Guide

이 저장소는 TarkovHelper의 유일한 작업 기준(source of truth)입니다. AI 에이전트나 ChatGPT가 작업할 때는 대화 기록보다 GitHub의 현재 브랜치, Draft PR, Actions 결과를 우선합니다.

## 세션 시작 순서

1. `main`의 최신 상태를 확인합니다.
2. 사용자가 진행 중인 작업을 이어 달라고 하면 가장 최근의 관련 Draft PR과 해당 브랜치를 먼저 확인합니다.
3. PR 설명의 `현재 상태`, `검증`, `남은 작업 / 다음 행동`, `인수인계`를 읽고 그 지점부터 계속합니다.
4. 관련 PR이 없을 때만 저장소 전체 탐색부터 시작합니다.

## 작업 단위

- 하나의 변경 작업은 `agent/<task-slug>` 브랜치 하나로 관리합니다.
- 작업이 한 대화를 넘길 가능성이 있으면 구현 완료를 기다리지 말고 Draft PR을 일찍 생성합니다.
- Draft PR 설명을 작업의 영구 인수인계 문서로 사용합니다.
- 의미 있는 단계가 끝날 때마다 PR 설명 또는 PR 댓글에 현재 상태와 다음 작업을 남깁니다. 대화가 갑자기 끝나더라도 GitHub만 읽으면 재개할 수 있어야 합니다.
- 특별한 지시가 없으면 `main`에 직접 수정하지 않습니다.

## 표준 검증 경로

로컬 실행보다 저장소의 GitHub Actions를 최종 기준으로 사용합니다. 현재 표준 빌드 워크플로는 `.github/workflows/build.yml`입니다.

기본 빌드 명령:

```powershell
dotnet restore TarkovHelper.sln
dotnet build TarkovHelper.sln --configuration Release --no-restore
```

PR에서는 기존 Actions가 다음을 검증합니다.

1. Release 빌드 및 아이콘 변환 self-test
2. deterministic database smoke 검증
3. live tarkov.dev 기반 external database smoke 검증
4. Windows `win-x64` release candidate publish
5. `TarkovHelper_JH.zip` 및 SHA256 생성

CI를 우회하기 위한 임시 유지보수 workflow나 테스트 완화는 최종 상태에 남기지 않습니다.

## 릴리즈 원칙

- 기존 릴리즈 자동화를 재사용하며 별도의 수동 배포 절차를 새로 만들지 않습니다.
- PR의 release candidate 검증이 통과한 뒤에만 병합 대상으로 봅니다.
- `main` 병합 후 기존 `Publish Release` 흐름과 `Verify Published Release` 검증 결과를 확인합니다.
- 릴리즈 관련 버전, 산출물 이름, `update.xml`, 릴리즈 노트가 서로 일치해야 합니다.

## 대화 길이와 무관한 연속 작업 규칙

대화 자체의 컨텍스트 한도는 제거할 수 없습니다. 대신 작업 상태를 대화 밖 GitHub에 지속적으로 기록하여 한도 도달이 작업 중단으로 이어지지 않게 합니다.

새 대화에서 사용자가 단순히 "타르코프 헬퍼 이어서"라고 해도, 관련 Draft PR의 브랜치·설명·Actions 상태를 읽은 뒤 계속할 수 있는 상태를 유지합니다.

세부 운영 절차는 `docs/AI_WORKFLOW.md`를 따릅니다.
