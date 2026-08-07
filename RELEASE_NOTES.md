<!-- version: 1.10.1 -->
## 타르코프헬퍼_JH 1.10.1

Escape from Tarkov PVP 진행도, 아이템, 은신처, 탄약 및 지도 정보를 관리하는 Windows용 도우미입니다.

### v1.10.1 수정
- 최신 tarkov.dev 퀘스트 목록에 검증된 tarkov-data-overlay 보정을 적용해 누락·오번역 퀘스트를 동기화했습니다.
- Ragman `New Beginning` Prestige 5/6를 포함한 명성 퀘스트와 요구 명성 레벨 매핑을 보정했습니다.
- `Neuanfang` 오번역 및 종료된 퀘스트가 유효 목록에 남는 문제를 방지했습니다.
- 보정 데이터가 없거나 형식이 불완전하면 기존 DB를 보존하고 업데이트를 중단하도록 fail-closed 검증을 강화했습니다.
- 정적 API 장애 시 보정 없는 GraphQL 퀘스트 카탈로그로 되돌아가지 않도록 차단했습니다.

### 설치
1. TarkovHelper_JH.zip을 원하는 폴더에 압축 해제합니다.
2. TarkovHelper_JH.exe를 실행합니다.
3. .NET 8 Windows Desktop Runtime이 필요합니다.

기존 user_data.db는 릴리즈 ZIP에 포함되지 않으며 업데이트 시 유지됩니다.
