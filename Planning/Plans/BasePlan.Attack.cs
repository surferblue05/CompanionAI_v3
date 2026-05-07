using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Planning.Planners;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Attack - Delegates to AttackPlanner

        protected PlannedAction PlanAttack(Situation situation, ref float remainingAP, BaseUnitEntity preferTarget = null,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanAttack(situation, ref remainingAP, RoleName, preferTarget, excludeTargetIds, excludeAbilityGuids);

        // ★ v3.8.44: AttackPhaseContext 전달 - 공격 실패 이유 기록
        protected PlannedAction PlanAttack(Situation situation, ref float remainingAP, AttackPhaseContext context,
            BaseUnitEntity preferTarget = null, HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanAttack(situation, ref remainingAP, RoleName, preferTarget, excludeTargetIds, excludeAbilityGuids, context);

        protected AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.SelectBestAttack(situation, target, excludeAbilityGuids);

        // ★ v3.8.44: AttackPhaseContext 전달 오버로드
        protected AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids, AttackPhaseContext context)
            => AttackPlanner.SelectBestAttack(situation, target, excludeAbilityGuids, context);

        protected PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => AttackPlanner.PlanPostMoveAttack(situation, target, ref remainingAP, RoleName);

        // ★ v3.1.24: 이동 목적지 기반 Post-move 공격
        protected PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP, Vector3? moveDestination)
            => AttackPlanner.PlanPostMoveAttack(situation, target, ref remainingAP, RoleName, moveDestination);

        protected PlannedAction PlanFinisher(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => AttackPlanner.PlanFinisher(situation, target, ref remainingAP, RoleName);

        protected PlannedAction PlanSpecialAbility(Situation situation, ref float remainingAP)
            => AttackPlanner.PlanSpecialAbility(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanSafeRangedAttack(Situation situation, ref float remainingAP,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanSafeRangedAttack(situation, ref remainingAP, RoleName, excludeTargetIds, excludeAbilityGuids);

        protected BaseUnitEntity FindLowHPEnemy(Situation situation, float threshold)
            => AttackPlanner.FindLowHPEnemy(situation, threshold);

        protected BaseUnitEntity FindWeakestEnemy(Situation situation, HashSet<string> excludeTargetIds = null)
            => AttackPlanner.FindWeakestEnemy(situation, excludeTargetIds);

        protected bool IsExcluded(BaseUnitEntity target, HashSet<string> excludeTargetIds)
            => AttackPlanner.IsExcluded(target, excludeTargetIds);

        protected bool IsAbilityExcluded(AbilityData ability, HashSet<string> excludeAbilityGuids)
            => AttackPlanner.IsAbilityExcluded(ability, excludeAbilityGuids);

        // ★ v3.1.16: AOE 공격 계획 (모든 Role에서 사용 가능)
        // ★ v3.117.19: effectiveCasterPosition 추가 — 이동 후 cast 가 plan 됐을 때 destination 기준 검사
        protected PlannedAction PlanAoEAttack(Situation situation, ref float remainingAP, UnityEngine.Vector3? effectiveCasterPosition = null)
            => AttackPlanner.PlanAoEAttack(situation, ref remainingAP, RoleName, effectiveCasterPosition);

        // ★ v3.1.29: Self-Targeted AOE 계획 (BladeDance 등)
        protected PlannedAction PlanSelfTargetedAoE(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // DangerousAoE 중 Self-Target 능력 찾기
            CollectionHelper.FillWhere(situation.AvailableAttacks, _tempAbilities,
                a => CombatAPI.IsSelfTargetedAoEAttack(a));

            // AvailableAttacks에서 필터링되었을 수 있으니 전체에서 다시 찾기
            if (_tempAbilities.Count == 0)
            {
                CollectionHelper.FillWhere(CombatAPI.GetAvailableAbilities(situation.Unit), _tempAbilities,
                    a => CombatAPI.IsSelfTargetedAoEAttack(a));
            }

            if (_tempAbilities.Count == 0) return null;

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var result = AttackPlanner.PlanSelfTargetedAoEAttack(situation, _tempAbilities[i], ref remainingAP, RoleName);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>★ v3.8.96: 유닛 타겟 AoE 공격 계획 (Burst/Scatter/기타 모든 유닛 타겟 AoE)
        /// Phase 4.3(Self), 4.3b(Melee), 4.4(Point)에서 처리하지 않는 나머지 모든 AoE</summary>
        protected PlannedAction PlanUnitTargetedAoE(Situation situation, ref float remainingAP, UnityEngine.Vector3? effectiveCasterPosition = null)
            => AttackPlanner.PlanUnitTargetedAoEAttack(situation, ref remainingAP, RoleName, effectiveCasterPosition);

        /// <summary>★ v3.9.08: AoE 재배치 — 아군 피격으로 AoE 차단 시 이동 후 AoE 시전</summary>
        protected (PlannedAction move, PlannedAction aoE) PlanAoEWithReposition(
            Situation situation, ref float remainingAP, ref float remainingMP)
            => AttackPlanner.PlanAoEWithReposition(situation, ref remainingAP, ref remainingMP, RoleName);

        /// ★ v3.8.50: 근접 AOE 계획 (유닛 타겟 근접 스플래시)
        protected PlannedAction PlanMeleeAoE(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // AvailableAttacks에서 근접 AOE 검색
            CollectionHelper.FillWhere(situation.AvailableAttacks, _tempAbilities,
                a => CombatAPI.IsMeleeAoEAbility(a));

            // DangerousAoE 필터로 제외되었을 수 있으니 전체에서 다시 찾기
            if (_tempAbilities.Count == 0)
            {
                CollectionHelper.FillWhere(CombatAPI.GetAvailableAbilities(situation.Unit), _tempAbilities,
                    a => CombatAPI.IsMeleeAoEAbility(a));
            }

            if (_tempAbilities.Count == 0) return null;

            return AttackPlanner.PlanMeleeAoEAttack(situation, ref remainingAP, RoleName);
        }

        #endregion
    }
}
