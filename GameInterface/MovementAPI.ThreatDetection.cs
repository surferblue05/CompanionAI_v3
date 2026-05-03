using System;
using System.Collections.Generic;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Parts;
using UnityEngine;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    public static partial class MovementAPI
    {
        #region Threat Detection

        /// <summary>
        /// ★ v3.0.62: AoE/함정 위협 점수 계산
        /// AiBrainHelper.TryFindThreats를 사용하여 해당 노드의 위협 평가
        /// </summary>
        public static float CalculateThreatScore(BaseUnitEntity unit, CustomGridNodeBase node)
        {
            if (unit == null || node == null) return 0f;

            float threatScore = 0f;

            try
            {
                var threats = AiBrainHelper.TryFindThreats(unit, node);
                if (threats == null) return 0f;

                // AoO 위협 (기습공격 유발)
                if (threats.aooUnits != null && threats.aooUnits.Count > 0)
                {
                    threatScore += threats.aooUnits.Count * 20f;
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Node has {threats.aooUnits.Count} AoO threats");
                }

                // ★ v3.8.88: TryFindThreats는 overwatchUnits를 안 채움 - PartOverwatch 직접 체크
                try
                {
                    int overwatchCount = 0;
                    foreach (var enemyInfo in unit.CombatGroup.Memory.Enemies)
                    {
                        var ow = enemyInfo.Unit?.GetOptional<PartOverwatch>();
                        if (ow != null && !ow.IsStopped && ow.OverwatchArea != null)
                        {
                            foreach (var owNode in ow.OverwatchArea)
                            {
                                if (owNode == node) { overwatchCount++; break; }
                            }
                        }
                    }
                    if (overwatchCount > 0)
                    {
                        threatScore += overwatchCount * 25f;
                        if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Node has {overwatchCount} Overwatch threats (direct check)");
                    }
                }
                catch { }

                // AoE 위협 (화염, 독가스 등)
                if (threats.aes != null && threats.aes.Count > 0)
                {
                    threatScore += threats.aes.Count * 30f;
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Node has {threats.aes.Count} AoE threats");
                }

                // 이동 시 데미지 AoE (화염 지대 등)
                if (threats.dmgOnMoveAes != null && threats.dmgOnMoveAes.Count > 0)
                {
                    threatScore += threats.dmgOnMoveAes.Count * 50f;
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Node has {threats.dmgOnMoveAes.Count} damage-on-move AoE");
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] CalculateThreatScore error");
            }

            return threatScore;
        }

        /// <summary>
        /// ★ v3.5.41: Larian Combat AI 방법론 - 경로 위험도 평가
        /// 시작점에서 끝점까지 경로 상의 모든 타일에 대해 위협 점수를 합산
        ///
        /// Larian AI 참조: MovementScore = A→B 경로상 PositionScore 합산
        /// 목적지만이 아닌 경로 전체의 위험도를 평가하여
        /// 안전한 경로를 선택하도록 유도
        /// </summary>
        /// <param name="unit">이동하는 유닛</param>
        /// <param name="startPos">시작 위치</param>
        /// <param name="endNode">목표 노드</param>
        /// <param name="pathCell">경로 셀 (경로 정보 포함)</param>
        /// <returns>경로 평균 위험도 (0 = 안전, 높을수록 위험)</returns>
        public static float EvaluatePathRisk(
            BaseUnitEntity unit,
            Vector3 startPos,
            CustomGridNodeBase endNode,
            WarhammerPathPlayerCell pathCell)
        {
            // ★ v3.8.13: AI 셀로 변환하여 통합 메서드 호출
            var aiCell = new WarhammerPathAiCell(
                pathCell.Position,
                pathCell.DiagonalsCount,
                pathCell.Length,
                pathCell.Node,
                pathCell.ParentNode,
                pathCell.IsCanStand,
                0, 0, 0  // 플레이어 셀에는 위협 데이터 없음
            );
            return EvaluatePathRiskAi(unit, startPos, endNode, aiCell);
        }

        /// <summary>
        /// ★ v3.8.13: AI 셀용 경로 위험도 평가 (실제 경로 위협 데이터 활용)
        /// AI 패스파인더가 계산한 ProvokedAttacks, EnteredAoE, StepsInsideDamagingAoE를 직접 사용
        ///
        /// 핵심: 게임의 AI 패스파인더는 경로 전체의 위협을 누적 계산하여 셀에 저장
        /// - ProvokedAttacks: 해당 경로로 이동 시 유발되는 총 AoO 수
        /// - EnteredAoE: 경로에서 진입하는 AoE 구역 수
        /// - StepsInsideDamagingAoE: 피해 AoE 내에서 이동하는 총 칸 수
        /// </summary>
        public static float EvaluatePathRiskAi(
            BaseUnitEntity unit,
            Vector3 startPos,
            CustomGridNodeBase endNode,
            WarhammerPathAiCell pathCell)
        {
            if (unit == null || endNode == null || pathCell.Node == null)
                return 0f;

            float totalRisk = 0f;

            try
            {
                // ★ v3.8.13: AI 셀의 경로 위협 데이터 직접 활용 (게임 패스파인더가 이미 계산함)
                // 이 값들은 경로 전체의 누적 위협이므로 별도 계산 불필요
                float pathProvokedAttacks = pathCell.ProvokedAttacks;
                float pathEnteredAoE = pathCell.EnteredAoE;
                float pathDamagingAoESteps = pathCell.StepsInsideDamagingAoE;

                // 경로 위협 점수 계산 (게임의 TileScorer와 유사한 가중치)
                // - AoO: 20점 (매우 위험 - 즉시 피해 + 행동 방해)
                // - AoE 진입: 15점 (위험 - 지속 피해 가능성)
                // - 피해 AoE 내 이동: 10점 (중간 - 매 칸 피해)
                totalRisk += pathProvokedAttacks * 20f;
                totalRisk += pathEnteredAoE * 15f;
                totalRisk += pathDamagingAoESteps * 10f;

                // ★ 디버그: 실제 경로 위협 데이터 로깅
                if (totalRisk > 0)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] PathRiskAi: AoO={pathProvokedAttacks}, AoE={pathEnteredAoE}, DmgAoE={pathDamagingAoESteps} -> Risk={totalRisk:F1}");
                }

                // ★ v3.110.16: InfluenceMap threat 누적 제거 — EvaluatePosition의 ThreatScore가 이미 커버.
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] EvaluatePathRiskAi error");
                return 0f;
            }

            return totalRisk;
        }

        /// <summary>
        /// ★ v3.5.41: 단순 거리 기반 경로 위험도 평가 (ParentNode 없을 때 사용)
        /// ★ v3.110.16: influenceMap 파라미터 제거. 현재 구현체는 샘플링 기반이지만 InfluenceMap.threat 조회에 의존했음.
        /// InfluenceMap 제거 후 별도 위협 평가 필요 시 AiBrainHelper.TryFindThreats 기반으로 재구성 가능.
        /// 지금은 단순화 — 경로 위험 평가는 EvaluatePathRiskAi (AI 셀 기반)가 주 경로.
        /// </summary>
        public static float EvaluatePathRiskSimple(
            BaseUnitEntity unit,
            Vector3 startPos,
            Vector3 endPos)
        {
            // ★ v3.110.16: InfluenceMap.GetThreatAt 기반 샘플링 제거. 현재 stub으로 0 반환.
            //   본래 Simple 경로는 ParentNode 없는 타일용 폴백이었으나 실제 EvaluatePathRiskAi가
            //   대부분 커버. 이 stub 유지는 호출 시그니처 안정성 목적.
            //   AI 셀 위협 데이터(ProvokedAttacks/EnteredAoE)가 없는 희귀 케이스에 한해 0 반환.
            return 0f;
        }

        /// <summary>
        /// ★ v3.6.7: 명중률 기반 위치 보너스 계산
        /// ★ v3.6.8: Scatter/근접 공격 예외 처리 추가
        /// 최적 사거리(무기 사거리의 절반 이내) 위치에 보너스 부여
        ///
        /// 거리 계수(Distance Factor):
        /// - 1.0 = 최적 거리 (사거리 절반 이내) → +15 보너스
        /// - 0.5 = 중간 거리 (절반~최대) → +5 보너스
        /// - 0.0 = 사거리 초과 → -10 패널티
        /// - Scatter/근접 → 항상 0 (100% 명중, 거리 무관)
        /// </summary>
        /// <param name="position">평가할 위치</param>
        /// <param name="enemies">적 목록</param>
        /// <param name="weaponRange">무기 사거리 (타일 단위)</param>
        /// <param name="isScatter">★ v3.6.8: Scatter 공격 여부 (100% 명중)</param>
        /// <param name="isMelee">★ v3.6.8: 근접 공격 여부 (100% 명중)</param>
        /// <returns>명중률 보너스 점수</returns>
        /// <summary>
        /// ★ v3.9.26: 게임의 실제 명중률(RuleCalculateHitChances) 기반 위치 보너스
        /// 가장 가까운 적 2명만 평가 (성능: 위치당 최대 2회 Rule 호출)
        /// primaryAttack null 시 기존 거리 밴드 폴백
        /// </summary>
        public static float CalculateHitChanceBonus(
            Vector3 position,
            List<BaseUnitEntity> enemies,
            float weaponRange,
            bool isScatter = false,
            bool isMelee = false,
            BaseUnitEntity attacker = null,
            AbilityData primaryAttack = null)
        {
            // ★ v3.6.8: Scatter/근접은 거리와 무관하게 100% 명중 → 보너스 불필요
            if (isScatter || isMelee)
                return 0f;

            if (enemies == null || enemies.Count == 0 || weaponRange <= 0)
                return 0f;

            try
            {
                // ★ v3.9.26: 실제 명중률 기반 보너스 (attacker + primaryAttack 사용 가능 시)
                if (attacker != null && primaryAttack != null)
                {
                    return CalculateActualHitChanceBonus(position, enemies, attacker, primaryAttack);
                }

                // 폴백: 거리 밴드 기반 보너스 (primaryAttack 없을 때)
                return CalculateDistanceBandBonus(position, enemies, weaponRange);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] CalculateHitChanceBonus error");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.9.26: 게임 룰 기반 실제 명중률 보너스
        /// 가장 가까운 적 2명에 대해 GetHitChanceFromPosition 사용
        /// hit% → 보너스 매핑: 80%+ → +20, 60%+ → +12, 40%+ → +5, 20%+ → -5, &lt;20% → -15
        /// </summary>
        private static float CalculateActualHitChanceBonus(
            Vector3 position,
            List<BaseUnitEntity> enemies,
            BaseUnitEntity attacker,
            AbilityData primaryAttack)
        {
            // 가장 가까운 적 2명 찾기
            float closestDist = float.MaxValue;
            float secondDist = float.MaxValue;
            BaseUnitEntity closest = null;
            BaseUnitEntity secondClosest = null;

            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (enemy == null || enemy.LifeState.IsDead) continue;

                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < closestDist)
                {
                    secondDist = closestDist;
                    secondClosest = closest;
                    closestDist = dist;
                    closest = enemy;
                }
                else if (dist < secondDist)
                {
                    secondDist = dist;
                    secondClosest = enemy;
                }
            }

            if (closest == null) return 0f;

            // 가장 가까운 적에 대한 명중률
            float bestBonus = HitChanceToBonus(CombatAPI.GetHitChanceFromPosition(primaryAttack, attacker, position, closest));

            // 두 번째 가까운 적이 있으면 추가 평가
            if (secondClosest != null)
            {
                float secondBonus = HitChanceToBonus(CombatAPI.GetHitChanceFromPosition(primaryAttack, attacker, position, secondClosest));
                // 두 적의 보너스 중 더 좋은 것 채택
                if (secondBonus > bestBonus) bestBonus = secondBonus;
            }

            return bestBonus;
        }

        /// <summary>
        /// ★ v3.110.8: 게임 공식 기반 연속 함수로 재설계.
        ///
        /// 근거:
        ///   RuleCalculateAbilityDistanceFactor — (d ≤ MaxD/2 → 1.0, d ≤ MaxD → 0.5, 초과 → 0)
        ///   RuleCalculateHitChances — hitChance ≈ (BS + 30) × DistanceFactor - Recoil + modifiers
        ///
        /// 이전 (v3.9.26 ~ v3.110.7): hitChance integer를 5단계 step으로 매핑 (80/60/40/20 → 20/12/5/-5/-15).
        ///   문제점:
        ///   1) 임의 임계값 — 79% vs 80%가 +8점 점프 (비연속)
        ///   2) Dodge/Parry가 하드 캡이라 실질 명중률은 대부분 ~95%/~50%/0% 3-state로 쏠림 → 5단계 무의미
        ///   3) Cover 효과가 hitChance에 녹아있어 EvaluatePosition의 CoverScore와 이중 카운팅 위험
        ///
        /// 현재: "위치가 직접 통제하는 축"인 DistanceFactor 단독 사용 (연속 0~1).
        ///   공식: (DistanceFactor - 0.5) × 40
        ///     1.0 (풀 명중률 영역) → +20
        ///     0.5 (반토막 영역, 중립) → 0
        ///     0.0 (사거리 초과) → -20
        ///   Cover는 EvaluatePosition.CoverScore에서 별도 처리 (중복 방지).
        /// </summary>
        private static float HitChanceToBonus(CombatAPI.HitChanceInfo hitInfo)
        {
            if (hitInfo == null) return 0f;
            return (hitInfo.DistanceFactor - 0.5f) * 40f;
        }

        /// <summary>
        /// ★ v3.9.26: 폴백 거리 밴드 기반 보너스 (primaryAttack 없을 때)
        /// ★ v3.110.8: optimalRange를 50%로 수정 — 게임 공식 RuleCalculateAbilityDistanceFactor의
        /// DistanceFactor 1.0 경계(MaxD/2)와 일치. 이전 0.6은 이미 DistanceFactor=0.5 반토막 영역.
        /// </summary>
        private static float CalculateDistanceBandBonus(
            Vector3 position,
            List<BaseUnitEntity> enemies,
            float weaponRange)
        {
            float bestBonus = -10f;
            float optimalRange = weaponRange * 0.5f;  // ★ v3.110.8: 0.6 → 0.5 (게임 공식)

            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (enemy == null || enemy.LifeState.IsDead) continue;

                float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(position, enemy.Position));

                float bonus;
                if (distTiles <= optimalRange)
                {
                    bonus = 15f;
                }
                else if (distTiles <= weaponRange)
                {
                    // 이차 감쇠 — 최대사거리 근처는 더 큰 페널티
                    float excess = (distTiles - optimalRange) / Math.Max(weaponRange - optimalRange, 1f);
                    bonus = 15f - (excess * excess) * 20f;  // 50%→15, 75%→10, 100%→-5
                }
                else
                {
                    bonus = -10f;
                }

                if (bonus > bestBonus)
                    bestBonus = bonus;
            }

            return bestBonus;
        }

        #endregion
    }
}
