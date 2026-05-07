using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// Phase 4 ThreatDifferential — 적이 *어떤 아군* 을 위협하는지 매핑.
    /// SituationAnalyzer 에서 1회 사전 계산 → ScoreEnemy 가 read-only 활용.
    ///
    /// 목적: "약한 적이라도 squishy DPS 옆 붙어있으면 kill 우선" 시나리오 구현.
    /// 기존 EvaluateThreat 는 *자기* 위협만 측정 (situation.Unit 거리). enemy→ally 매핑 없음.
    ///
    /// 빌딩 블록:
    /// - CombatAPI.GetEnemyTurnThreatScore(enemy, pos) — 적이 다음 턴에 pos 를 칠 수 있나 (0/0.5/1)
    /// - EnemyMoveCache (Harmony 캡처) — 게임이 사전 계산한 적 도달 노드
    ///
    /// 비용: enemies × allies 매트릭스 (8×5 = 40 호출 / replan). 캐시 활용으로 가벼움.
    /// </summary>
    public class EnemyTargetingMap
    {
        // enemy → 가장 위협받는 ally + 점수 (squishy threat = threat × vulnerability)
        private readonly Dictionary<BaseUnitEntity, ThreatenedAllyInfo> _enemyToAlly
            = new Dictionary<BaseUnitEntity, ThreatenedAllyInfo>();

        // raw threat matrix — (enemy, ally) → threat score 0/0.5/1
        private readonly Dictionary<(BaseUnitEntity, BaseUnitEntity), float> _threatMatrix
            = new Dictionary<(BaseUnitEntity, BaseUnitEntity), float>();

        // ally vulnerability cache (HP% based)
        private readonly Dictionary<BaseUnitEntity, float> _vulnerabilityCache
            = new Dictionary<BaseUnitEntity, float>();

        public struct ThreatenedAllyInfo
        {
            public BaseUnitEntity Ally;
            public float SquishyThreatScore;  // = threat × vulnerability, max over allies
        }

        private EnemyTargetingMap() { }

        /// <summary>
        /// SituationAnalyzer 에서 호출 — enemies × allies 매트릭스 사전 계산.
        /// </summary>
        public static EnemyTargetingMap Compute(
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            var map = new EnemyTargetingMap();
            if (enemies == null || allies == null || enemies.Count == 0 || allies.Count == 0)
                return map;

            // ally vulnerability 사전 계산 — 합성 (HP% × Role × MaxHP class).
            //   baseVuln (HP%): 0.5 ~ 1.0
            //   roleMultiplier: Tank=0.7, Sturdy(MaxHP>100)=0.8, Squishy(Support|MaxHP<70)=1.3, default=1.0
            //   final = clamp(base × mult, 0.3, 1.3)
            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;

                float hpPercent = CombatCache.GetHPPercent(ally);
                float baseVuln = 0.5f + (1f - hpPercent / 100f) * 0.5f;

                // Role + MaxHP 분류 (per-Compute 1회 호출, 캐시 불필요 — 5 ally 만 있음)
                AIRole role = RoleDetector.DetectOptimalRole(ally);
                int maxHP = CombatAPI.GetActualMaxHP(ally);

                float roleMultiplier = 1.0f;
                if (role == AIRole.Tank)
                    roleMultiplier = 0.7f;          // 탱크 = 단단함, 위협 보호 우선순위 ↓
                else if (maxHP >= 100)
                    roleMultiplier = 0.8f;          // 큰 MaxHP = sturdy
                else if (role == AIRole.Support || maxHP < 70)
                    roleMultiplier = 1.3f;          // squishy: caster/healer/낮은 MaxHP

                float vulnerability = System.Math.Max(0.3f, System.Math.Min(1.3f, baseVuln * roleMultiplier));
                map._vulnerabilityCache[ally] = vulnerability;

                if (Main.IsDebugEnabled)
                    Log.Analysis.Debug($"[EnemyTargetingMap] {ally.CharacterName}: vulnerability={vulnerability:F2} (HP%={hpPercent:F0}, role={role}, maxHP={maxHP})");
            }

            // enemy × ally 매트릭스 빌드
            int totalThreats = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                BaseUnitEntity bestThreatenedAlly = null;
                float bestSquishyScore = 0f;

                foreach (var ally in allies)
                {
                    if (ally == null || !ally.IsConscious) continue;

                    float threat = CombatAPI.GetEnemyTurnThreatScore(enemy, ally.Position);
                    if (threat <= 0f) continue;  // 도달 불가 → 매트릭스 미저장 (기본 0)

                    map._threatMatrix[(enemy, ally)] = threat;
                    totalThreats++;

                    float vuln = map._vulnerabilityCache.TryGetValue(ally, out var v) ? v : 0.5f;
                    float squishyScore = threat * vuln;
                    if (squishyScore > bestSquishyScore)
                    {
                        bestSquishyScore = squishyScore;
                        bestThreatenedAlly = ally;
                    }
                }

                if (bestThreatenedAlly != null)
                {
                    map._enemyToAlly[enemy] = new ThreatenedAllyInfo
                    {
                        Ally = bestThreatenedAlly,
                        SquishyThreatScore = bestSquishyScore
                    };
                }
            }

            if (Main.IsDebugEnabled)
                Log.Analysis.Debug($"[EnemyTargetingMap] {map._enemyToAlly.Count}/{enemies.Count} enemies threatening allies, {totalThreats} threat pairs computed");

            return map;
        }

        /// <summary>
        /// 적 e 가 가장 위협하는 아군. 위협 없으면 null.
        /// </summary>
        public BaseUnitEntity GetMostThreatenedAlly(BaseUnitEntity enemy)
        {
            if (enemy == null) return null;
            return _enemyToAlly.TryGetValue(enemy, out var info) ? info.Ally : null;
        }

        /// <summary>
        /// (enemy, ally) raw threat 0/0.5/1. EnemyMoveCache 기반.
        /// </summary>
        public float GetThreatScore(BaseUnitEntity enemy, BaseUnitEntity ally)
        {
            if (enemy == null || ally == null) return 0f;
            return _threatMatrix.TryGetValue((enemy, ally), out var s) ? s : 0f;
        }

        /// <summary>
        /// ally 의 취약도 0.5~1.0 (현재 HP% 기반).
        /// </summary>
        public float GetAllyVulnerability(BaseUnitEntity ally)
        {
            if (ally == null) return 0f;
            return _vulnerabilityCache.TryGetValue(ally, out var v) ? v : 0.5f;
        }

        /// <summary>
        /// ★ 핵심 소비자 인터페이스: enemy 의 "squishy 보호 우선" 종합 점수 0~1.
        /// = max over allies (threat(e,a) × vulnerability(a))
        /// TargetScorer.ScoreEnemy 가 weight × multiplier 곱해서 score 가산.
        /// </summary>
        public float GetSquishyThreatScore(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0f;
            return _enemyToAlly.TryGetValue(enemy, out var info) ? info.SquishyThreatScore : 0f;
        }

        public int ThreatenedAllyCount => _enemyToAlly.Count;
    }
}
