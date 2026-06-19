# 위치 평가 정확성·스마트함 감사 (2026-06-20)

> 계기: 인게임 관찰 — 카시아(Support 사이커)가 안전하지 않은 위치로 이동. 3개 병렬 조사 에이전트(우리 코드 정확성 / 게임 TileScorer 활용 / 웹 최신기법) 교차 검증.

## 관찰 (로그 증거)
카시아: Support, 19 MP, 유일 공격 = Lidless Stare(10타일 directional AoE)가 **항상 아군 차단**(Hittable=0). 그런데도 `FindRangedAttackPositionSync ← PlanMoveToEnemy`로 적 3.6타일/엄폐없음/적시야1 위치로 접근.

## 근본 원인 (정확성 버그 2개)

### PRIMARY — 라우팅: "쓸 공격 없는 유닛"이 안전 포지셔너 대신 접근 fallback
- 가드 `noAttackNoApproach = PrefersRanged && AvailableAttacks.Count == 0` (`SupportPlan.cs` + DPS/Tank/Overseer 미러)는 공격을 **보유**했는지만 검사. Hittable=0(못 씀)을 구분 못 함.
- 결과: 안전 재배치(Phase 8.7 `PlanTacticalReposition → FindRetreatPositionSync`) 스킵 → Phase 9 `PlanMoveToEnemy` → `FindBestApproachPosition`(BestPositionFinding.cs) = **순수 "적에게 최대한 가까이", 안전 점수 0**.
- ⚠️ 단순 `|| !HasHittableEnemies` 추가는 *사거리 밖이라 접근이 필요한* 정상 원거리 유닛까지 안전모드로 보냄 → **접근 가능성(FindRangedAttackPosition null = 도달 가능 hittable 위치 없음) + Role(squishy)** 기준으로 정교화 필요.

### SECONDARY — C9 단위 (미터 vs 타일)
- `SituationAnalyzer.cs:256-257` `NearestEnemyDistance = CombatCache.GetDistance`(미터) vs `MinSafeDistance`(타일)를 ~12곳에서 직접 비교 → 약 ×1.35 "더 안전" 오판 → 후퇴 트리거 억제.

## 더 스마트하게 — 핵심은 "결합 규칙" (3 출처 동의)
- 우리: `TotalScore = 가중합`의 최댓값 → 선형합은 "안전 0 + 공격 높음" 타일을 막을 수 없음(수학적 한계).
- **게임 자체 AI**: lexicographic `Score` tuple — 방어 brain은 `ThreatsScore→HideScore→StayingAway`만, 공격이 안전을 못 뒤집음.
- **업계**(Unreal EQS / Dave Mark IAUS / XCOM): 안전은 **veto**. (a) 점수 전 생존가능 하드필터, (b) 곱셈 utility(안전0→전체0), (c) 안전우선 tiered.

## 하지 말 것 (조사로 배제)
- 게임 TileScorer **직접 위임 ✗** — DecisionContext 결합/AiConsideredMoveVariants 비용/scorer statefulness/async 데드락(Phase 5 실패 전례). "공식 포팅"(TileScorerPort: HideScore/StayingAway/EnemyTurnThreat/BodyGuard 이미 적용) 방식이 정답.
- 아키텍처 재작성 ✗ — archetype별 가중합 = XCOM 방식. 바꿀 건 *결합 규칙*뿐.
- `InfluenceMap` 재도입 시 주의 — v3.110/111에서 의도적 제거됨. 위협 필드는 *행동 시스템*이 아니라 *memoization 캐시*로만.

## 로드맵 (각 단계 인게임 검증)
- **Phase 1 정확성 (저위험)**: 1a 라우팅(무-viable-공격 유닛 → 안전 포지셔너, 접근필요 케이스 보존), 1b C9 단위(`NearestEnemyDistance`→타일 + ~12 소비처/로그 감사).
- **Phase 2 스마트함 (중위험)**: 방어 목표(Retreat/Cover/ranged-kite)에 안전 veto/lexicographic 비교 (`MaxBy(TotalScore)` → goal별 IComparer). 잠자던 `ThreatsScore`(기회공격+AoE진입) 활성화. squishy Role 안전우선 weight 프로파일.
- **Phase 3 비용 (선택)**: 다중소스 Dijkstra 위협 필드(적 시드 1회 flood → 타일당 O(1)) + 재계획 증분 패치 + 저비용 축 우선 early-exit.

## 참조
- 게임 TileScorer 지도: memory `game_ai_scoring_system.md`
- 핵심 출처: Game AI Pro 3 Ch.13 (Mike Lewis, utility veto + early-out), Game AI Pro Ch.26 (EQS filter-before-score), XCOM2 `XComAI.ini`(archetype별 weight).
