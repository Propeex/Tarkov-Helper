from pathlib import Path

path = Path('.github/workflows/release.yml')
text = path.read_text(encoding='utf-8')
old = '''          ### v1.8.4 수정
          - 미니맵을 켠 상태에서 퀘스트·은신처·아이템·총알·스캐너 탭으로 이동해도 미니맵 창과 추적 기능이 유지되도록 수정
          - 일반 탭 전환과 프로필 전환·앱 종료를 구분하여 페이지 교체와 종료 시에는 기존 자원 정리를 유지
          - 반복적인 지도 탭 진입과 이탈에서도 미니맵 이벤트 구독이 중복되지 않도록 정규화
'''
new = '''          ### v1.10.1 수정
          - 최신 tarkov.dev 퀘스트 목록에 검증된 tarkov-data-overlay 보정을 적용해 누락·오번역 퀘스트를 동기화
          - Ragman `New Beginning` Prestige 5/6를 포함한 명성 퀘스트와 요구 명성 레벨 매핑을 보정
          - `Neuanfang` 오번역 및 종료된 퀘스트가 유효 목록에 남는 문제를 방지
          - 보정 데이터가 없거나 형식이 불완전하면 기존 DB를 보존하고 업데이트를 중단하도록 fail-closed 검증을 강화
          - 정적 API 장애 시 보정 없는 GraphQL 퀘스트 카탈로그로 되돌아가지 않도록 차단
'''
if old not in text:
    raise RuntimeError('release note block not found')
path.write_text(text.replace(old, new, 1), encoding='utf-8', newline='\n')
print('v1.10.1 release notes updated')
