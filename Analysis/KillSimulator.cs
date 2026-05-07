using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.Designers.Mechanics.Facts.Damage;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.2.30: 다중 능력 조합 킬 시뮬레이션
    /// 개별 능력으로는 1타킬 불가능하지만 조합으로 확정 킬 가능한 시퀀스 탐색
    /// </summary>
    public static class KillSimulator
    {
        /// <summary>
        /// 킬 시퀀스 결과
        /// </summary>
        public class KillSequence
        {
            public BaseUnitEntity Target { get; set; }
            public List<AbilityData> Abilities { get; set; } = new List<AbilityData>();
            public float TotalDamage { get; set; }
            public float TargetHP { get; set; }
            // 이론적 데미지 ≥ HP — 모든 타격 명중 가정 (낙관적, 명중률 미반영)
            public bool IsConfirmedKill => TotalDamage >= TargetHP;
            public float APCost { get; set; }
            public float Efficiency => APCost > 0 && IsConfirmedKill ? (TotalDamage / APCost) : 0f;

            // ★ v3.117.0: 명중률 통합 (Phase A) — 실제 마무리 확률
            //   체인의 모든 공격이 명중해야 시퀀스 성공 → Π hitChance_i
            //   (단일 공격의 경우 hitChance × P(damage ≥ HP | hit) — TrySingleAbilityKill 에서 직접 설정)
            public float KillProbability { get; set; } = 1f;
            // 명중 확률 가중 효율 — caller 의 weighted 보너스 계산용
            public float ExpectedEfficiency => APCost > 0 ? (KillProbability * TotalDamage / APCost) : 0f;
            // 진짜 마무리 가능성 — 0.85 이상이면 "거의 확정"
            public bool IsHighProbabilityKill => KillProbability >= 0.85f;
        }

        /// <summary>
        /// 타겟에 대해 킬 확정 시퀀스 탐색
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="target">타겟 유닛</param>
        /// <param name="maxAbilities">최대 능력 조합 수 (기본 3)</param>
        /// <returns>킬 시퀀스 (IsConfirmedKill로 확정 킬 여부 확인)</returns>
        public static KillSequence FindKillSequence(
            Situation situation,
            BaseUnitEntity target,
            int maxAbilities = 3)
        {
            if (situation == null || target == null)
                return null;

            try
            {
                // ★ v3.4.01: P0-1 null 체크 추가
                if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                    return null;

                // 타겟 현재 HP 계산
                // ★ v3.5.00: GetHP → GetActualHP
                float targetHP = CombatAPI.GetActualHP(target);
                if (targetHP <= 0)
                    return null;

                // 타겟에게 사용 가능한 공격 능력들
                // ★ v3.6.10: Point 타겟 AOE 높이 체크 추가
                var attacks = situation.AvailableAttacks
                    .Where(a => {
                        // AOE 높이 체크
                        if (CombatAPI.IsPointTargetAbility(a))
                        {
                            if (!CombatAPI.IsAoEHeightInRange(a, situation.Unit, target))
                            {
                                Log.Analysis.Debug($"[KillSimulator] AOE height failed: {a.Name} -> {target.CharacterName}");
                                return false;
                            }
                        }
                        return CombatAPI.CanUseAbilityOn(a, new TargetWrapper(target), out _);
                    })
                    .ToList();

                if (attacks.Count == 0)
                    return null;

                // 1. 단일 능력 1타킬 체크 (가장 효율적)
                // ★ v3.5.78: situation 전달하여 AOE 보너스 고려
                var singleKillSequence = TrySingleAbilityKill(situation, attacks, target, targetHP);
                if (singleKillSequence != null)
                    return singleKillSequence;

                // 2. 버프 + 공격 조합 시뮬레이션
                var buffSequence = SimulateWithBuffs(situation, target, attacks, targetHP);
                if (buffSequence != null && buffSequence.IsConfirmedKill)
                    return buffSequence;

                // 3. 다중 공격 조합 시뮬레이션
                var multiAttackSequence = SimulateMultiAttack(situation, target, attacks, targetHP, maxAbilities);
                return multiAttackSequence;
            }
            catch (Exception ex)
            {
                Log.Analysis.Error(ex, $"[KillSimulator] Error");
                return null;
            }
        }

        /// <summary>
        /// 단일 능력으로 1타킬 가능한지 체크
        /// ★ v3.5.78: AOE 보너스를 고려하여 최적 킬 능력 선택 (첫 번째가 아닌 최고 점수)
        /// </summary>
        private static KillSequence TrySingleAbilityKill(
            Situation situation,
            List<AbilityData> attacks,
            BaseUnitEntity target,
            float targetHP)
        {
            KillSequence bestSequence = null;
            float bestScore = float.MinValue;

            foreach (var attack in attacks)
            {
                var (minDamage, maxDamage, _) = CombatAPI.GetDamagePrediction(attack, target);
                if (maxDamage <= 0) continue;

                // ★ v3.117.0 Phase A: 명중률 + 데미지 분포 기반 실제 킬 확률
                //   기존: minDamage ≥ HP 만 "확정 킬" 처리 — 명중률 무시 + 분산 폭 무시
                //   현재: hitChance × P(damage ≥ HP | hit) 로 진짜 확률 계산
                float pKillIfHit;
                if (minDamage >= targetHP) pKillIfHit = 1f;                              // 빗나가지만 않으면 확정
                else if (maxDamage >= targetHP)
                {
                    float range = Math.Max(1f, maxDamage - minDamage);
                    pKillIfHit = (maxDamage - targetHP) / range;                          // 균일 분포 가정
                }
                else continue;                                                            // 1타킬 불가 — 다음 능력

                float hitChance = GetHitChance01(situation?.Unit, target, attack);
                float killProb = hitChance * pKillIfHit;

                float avgDamage = (minDamage + maxDamage) / 2f;
                float apCost = attack.CalculateActionPointCost();
                // 효율 — KillProbability 가중 (낮은 명중률은 자동 페널티)
                float efficiency = (killProb * avgDamage) / Math.Max(apCost, 0.1f);

                // ★ v3.5.78: AOE 보너스 계산 - 게임 API로 정확한 타일 감지
                float aoeBonus = 0f;
                if (situation != null)
                {
                    int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                        attack,
                        target.Position,
                        situation.Unit.Position,
                        situation.Enemies);

                    int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);
                    aoeBonus = additionalEnemies * 15f;

                    if (additionalEnemies > 0)
                    {
                        Log.Analysis.Debug($"[KillSimulator] Single-kill AOE: {attack.Name} " +
                            $"hits {aoeEnemyCount} enemies → +{aoeBonus:F0} bonus");
                    }
                }

                float totalScore = efficiency + aoeBonus;

                // 더 좋은 킬 능력?
                if (totalScore > bestScore)
                {
                    bestScore = totalScore;
                    bestSequence = new KillSequence
                    {
                        Target = target,
                        Abilities = new List<AbilityData> { attack },
                        TotalDamage = avgDamage,
                        TargetHP = targetHP,
                        APCost = apCost,
                        KillProbability = killProb
                    };
                }
            }

            return bestSequence;
        }

        // ★ v3.117.0 Phase A: hit chance helper — 0..1 범위 + null safe
        //   Why: KillSimulator 내부에서 명중률 계산을 일관된 방식으로 (CombatAPI.GetHitChance 는 % 정수 반환)
        private static float GetHitChance01(BaseUnitEntity caster, BaseUnitEntity target, AbilityData ability)
        {
            if (caster == null || target == null || ability == null) return 1f;
            try
            {
                var info = CombatAPI.GetHitChance(ability, caster, target);
                if (info == null) return 1f;
                return Math.Max(0f, Math.Min(1f, info.HitChance / 100f));
            }
            catch
            {
                // hit chance 산정 실패는 무음 — 기본 1 (현재 동작 유지) 로 폴백
                return 1f;
            }
        }

        /// <summary>
        /// 버프 + 공격 조합으로 킬 가능한지 시뮬레이션
        /// ★ v3.5.82: AOE 보너스를 고려하여 최적 조합 선택
        /// </summary>
        private static KillSequence SimulateWithBuffs(
            Situation situation,
            BaseUnitEntity target,
            List<AbilityData> attacks,
            float targetHP)
        {
            // 공격 버프 능력 찾기 (PreAttackBuff)
            // ★ v3.5.00: AvailableAbilities → AvailableBuffs
            var attackBuffs = situation.AvailableBuffs
                .Where(a => AbilityDatabase.GetTiming(a) == AbilityTiming.PreAttackBuff)
                .ToList();

            if (attackBuffs.Count == 0)
                return null;

            // ★ v3.5.82: 모든 킬 가능 조합을 비교하여 최적 선택
            KillSequence bestSequence = null;
            float bestScore = float.MinValue;

            foreach (var buff in attackBuffs)
            {
                float buffMultiplier = EstimateBuffMultiplier(buff);
                float buffAPCost = buff.CalculateActionPointCost();

                foreach (var attack in attacks)
                {
                    var (minDamage, maxDamage, _) = CombatAPI.GetDamagePrediction(attack, target);
                    float avgDamage = (minDamage + maxDamage) / 2f;
                    float buffedDamage = avgDamage * buffMultiplier;

                    // 버프 적용 후 킬 가능
                    if (buffedDamage >= targetHP)
                    {
                        float attackAPCost = attack.CalculateActionPointCost();
                        float totalAPCost = buffAPCost + attackAPCost;

                        // AP가 충분한지 확인
                        if (situation.CurrentAP >= totalAPCost)
                        {
                            // ★ v3.117.0 Phase A: 명중률 통합 — 버프 자체는 100% 적용 가정 (자기 적용),
                            //   공격만 명중률 영향. killProbability = hitChance × P(buffedDamage ≥ HP | hit)
                            //   buffedDamage 가 이미 HP 이상이므로 P = 1 가정 (분산 추정 어려움 — buff multiplier 가 분산도 키움)
                            float hitChance = GetHitChance01(situation.Unit, target, attack);
                            float killProb = hitChance;

                            float efficiency = (killProb * buffedDamage) / Math.Max(totalAPCost, 0.1f);

                            // ★ v3.5.82: AOE 보너스 계산
                            float aoeBonus = 0f;
                            int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                                attack,
                                target.Position,
                                situation.Unit.Position,
                                situation.Enemies);

                            int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);
                            aoeBonus = additionalEnemies * 15f;

                            float totalScore = efficiency + aoeBonus;

                            // 더 좋은 조합?
                            if (totalScore > bestScore)
                            {
                                bestScore = totalScore;
                                bestSequence = new KillSequence
                                {
                                    Target = target,
                                    Abilities = new List<AbilityData> { buff, attack },
                                    TotalDamage = buffedDamage,
                                    TargetHP = targetHP,
                                    APCost = totalAPCost,
                                    KillProbability = killProb
                                };

                                if (additionalEnemies > 0)
                                {
                                    Log.Analysis.Debug($"[KillSimulator] Buff+Attack AOE: {buff.Name} + {attack.Name} " +
                                        $"hits {aoeEnemyCount} enemies → +{aoeBonus:F0} bonus");
                                }
                            }
                        }
                    }
                }
            }

            if (bestSequence != null)
            {
                Log.Analysis.Debug($"[KillSimulator] Buff+Attack kill: {bestSequence.Abilities[0].Name} + {bestSequence.Abilities[1].Name} " +
                    $"= {bestSequence.TotalDamage:F0} dmg >= {targetHP:F0} HP (score={bestScore:F0})");
            }

            return bestSequence;
        }

        /// <summary>
        /// 다중 공격 조합으로 킬 가능한지 시뮬레이션
        /// ★ v3.5.77: AOE 클러스터 보너스 추가 - 게임 API 기반 정확한 타일 감지
        /// </summary>
        private static KillSequence SimulateMultiAttack(
            Situation situation,
            BaseUnitEntity target,
            List<AbilityData> attacks,
            float targetHP,
            int maxAbilities)
        {
            // 그리디 방식: 높은 데미지 능력부터 누적
            // ★ v3.5.77: AOE 보너스를 효율 계산에 반영
            var sortedAttacks = attacks
                .Select(a => {
                    var (min, max, _) = CombatAPI.GetDamagePrediction(a, target);
                    float damage = (min + max) / 2f;
                    float apCost = a.CalculateActionPointCost();

                    // ★ AOE 클러스터 보너스: 게임 API로 실제 영향 받는 적 수 계산
                    int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                        a,
                        target.Position,
                        situation.Unit.Position,
                        situation.Enemies);

                    // 타겟 본인 제외한 추가 적 수
                    int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);

                    // 추가 적당 15점 보너스 (효율 점수에 가산)
                    float aoeBonus = additionalEnemies * 15f;

                    if (additionalEnemies > 0)
                    {
                        Log.Analysis.Debug($"[KillSimulator] AOE bonus: {a.Name} hits {aoeEnemyCount} enemies (+{additionalEnemies} additional) → +{aoeBonus:F0} efficiency");
                    }

                    return new {
                        Attack = a,
                        Damage = damage,
                        APCost = apCost,
                        AoEBonus = aoeBonus,
                        AdditionalEnemies = additionalEnemies
                    };
                })
                .Where(x => x.Damage > 0)
                .OrderByDescending(x => (x.Damage / Math.Max(x.APCost, 0.1f)) + x.AoEBonus) // 효율 + AOE 보너스 순
                .ToList();

            if (sortedAttacks.Count == 0)
                return null;

            var sequence = new KillSequence { Target = target, TargetHP = targetHP };
            float remainingAP = situation.CurrentAP;
            float totalDamage = 0f;
            // ★ v3.117.0 Phase A: 체인 모든 공격 명중 확률 누적 — Π hitChance_i
            //   다중 공격은 모든 발이 적중해야 누적 데미지가 HP 도달 → 체인 곱셈 모델
            float chainKillProb = 1f;
            var usedAbilities = new HashSet<string>();

            foreach (var item in sortedAttacks)
            {
                // 최대 능력 수 제한
                if (sequence.Abilities.Count >= maxAbilities)
                    break;

                // AP 부족
                if (remainingAP < item.APCost)
                    continue;

                // 같은 능력 중복 사용 방지 (쿨다운 고려)
                string abilityId = item.Attack.Blueprint?.AssetGuid?.ToString() ?? item.Attack.Name;
                if (usedAbilities.Contains(abilityId))
                    continue;

                float thisHit = GetHitChance01(situation.Unit, target, item.Attack);
                chainKillProb *= thisHit;

                usedAbilities.Add(abilityId);
                sequence.Abilities.Add(item.Attack);
                sequence.APCost += item.APCost;
                totalDamage += item.Damage;
                remainingAP -= item.APCost;

                // 킬 확정 (이론적 — 모두 명중 시)
                if (totalDamage >= targetHP)
                {
                    sequence.TotalDamage = totalDamage;
                    sequence.KillProbability = chainKillProb;
                    Log.Analysis.Debug($"[KillSimulator] Multi-attack kill: {sequence.Abilities.Count} abilities = {totalDamage:F0} dmg >= {targetHP:F0} HP, P(kill)={chainKillProb:F2}");
                    return sequence;
                }
            }

            // 킬 불가능해도 최선의 시도 반환
            sequence.TotalDamage = totalDamage;
            sequence.KillProbability = chainKillProb * (totalDamage / Math.Max(1f, targetHP));  // partial kill 도 비례
            return sequence;
        }

        /// <summary>
        /// 버프 능력의 데미지 증가 배율 추정
        /// ★ v3.4.01: P2-2 Blueprint 기반 분석 + 이름 기반 휴리스틱
        /// ★ v3.117.0 Phase B: WarhammerDamageModifier ContextValue 실제 값 읽기 — 매직 1.3 폐기
        /// </summary>
        // ★ v3.10.0: private → internal (TurnStrategyPlanner에서 재사용)
        internal static float EstimateBuffMultiplier(AbilityData buff)
        {
            if (buff?.Blueprint == null)
                return 1.25f;

            try
            {
                var components = buff.Blueprint.ComponentsArray;
                if (components == null) return 1.2f;

                // ★ v3.117.0 Phase B: 컴포넌트별 정확한 곱연산 누적
                //   PercentDamageModifier (+%) → 1 + sum(percent/100)
                //   UnmodifiablePercentDamageModifier → 1 + sum(percent/100)
                //   MinimumDamage / MaximumDamage flat 추가는 데미지 평균 환산 어려움 (HP/avgDamage 의존) — 보수적 무시
                //   WarhammerCriticalDamageModifier → 평균 +20% (crit 확률 가정 평균) — 폴백 1.2 유지
                //   감지된 게 없으면 1.0 (버프 효과 미상 → 명색만 버프 효과)
                float multiplier = 1f;
                bool foundAny = false;

                foreach (var component in components)
                {
                    if (component == null) continue;

#pragma warning disable 0612
                    if (component is WarhammerDamageModifier dmgMod)
                    {
                        // PercentDamageModifier 가 본격적인 % 보너스 — 직접 읽기
                        float pct = ReadSimpleContextValue(dmgMod.PercentDamageModifier);
                        float pctUnmod = ReadSimpleContextValue(dmgMod.UnmodifiablePercentDamageModifier);
                        if (pct != 0f || pctUnmod != 0f)
                        {
                            multiplier *= 1f + (pct + pctUnmod) / 100f;
                            foundAny = true;
                            Log.Analysis.Debug($"[KillSimulator] {component.GetType().Name}: pct={pct}+{pctUnmod} → ×{1f + (pct + pctUnmod) / 100f:F2}");
                        }
                        else
                        {
                            // ContextValue 가 Simple 이 아니거나 읽기 실패 — 보수적 1.15 가정 (완화된 폴백)
                            multiplier *= 1.15f;
                            foundAny = true;
                            Log.Analysis.Debug($"[KillSimulator] {component.GetType().Name}: complex ContextValue → fallback ×1.15");
                        }
                    }
                    else if (component is WarhammerDamageBonusAgainstSize ||
                             component is WarhammerModifyOutgoingAttackDamage)
                    {
                        // 별도 타입 — 정확한 추출 어려움. 보수적 ×1.15 유지 (기존 1.3 보다 완화)
                        multiplier *= 1.15f;
                        foundAny = true;
                        Log.Analysis.Debug($"[KillSimulator] {component.GetType().Name}: legacy → fallback ×1.15");
                    }
                    else if (component is WarhammerCriticalDamageModifier)
                    {
                        // Crit damage modifier — 평균 ~20% 가산 가정 (crit 확률 변수, 단순화)
                        multiplier *= 1.1f;
                        foundAny = true;
                        Log.Analysis.Debug($"[KillSimulator] {component.GetType().Name}: critical → ×1.1");
                    }
#pragma warning restore 0612
                }

                if (foundAny) return multiplier;
            }
            catch (Exception ex)
            {
                Log.Analysis.Error(ex, $"[KillSimulator] EstimateBuffMultiplier component analysis error");
            }

            // 어떤 데미지 컴포넌트도 없으면 1.0 (버프지만 데미지 영향 없음 → 호출 측에서 multiplier=1 로 무시 가능)
            return 1f;
        }

        // ★ v3.117.0 Phase B: ContextValue Simple 일 때만 직접 값 추출 (Calculate 호출 없이)
        //   Why: PropertyContext 미설정 상태에서 Calculate 호출 시 NRE/잘못된 값. Simple value 만 read-safe.
        //   Rank/Property/Shared 같은 동적 값은 0 반환 → caller 가 폴백 multiplier 사용
        private static float ReadSimpleContextValue(Kingmaker.UnitLogic.Mechanics.ContextValueModifier mod)
        {
            if (mod == null || !mod.Enabled) return 0f;
            try
            {
                // ContextValueModifier 는 ContextValue 상속 — IsValueSimple 이면 .Value 직접 읽기
                if (mod.IsValueSimple) return mod.Value;
                return 0f;  // 동적 값 — 호출 측이 폴백 사용
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 특정 타겟에 대해 확정 킬이 가능한지 빠르게 체크
        /// (전체 시퀀스 계산 없이 가능성만 확인)
        /// </summary>
        public static bool CanConfirmKill(Situation situation, BaseUnitEntity target)
        {
            var sequence = FindKillSequence(situation, target);
            return sequence != null && sequence.IsConfirmedKill;
        }
    }
}
