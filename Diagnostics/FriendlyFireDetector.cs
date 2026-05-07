using System;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;
using Kingmaker.RuleSystem.Rules;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.117.12: 친선 사격 진단 — 게임 native IWarhammerAttackHandler 활용.
    ///
    /// 동작:
    /// - 게임 EventBus 가 모든 attack 결과 (`RulePerformAttack`) broadcast
    /// - `HandleAttack` 에서 ResultIsHit + IsAlly 검사하여 친선 사격 감지
    /// - 우리 동료 (player party) 가 다른 동료를 hit 한 케이스 로그
    ///
    /// 기준 디컴파일:
    ///   `Kingmaker.Tutorial.Triggers.TutorialTriggerFriendlyFire` 가 동일 패턴 사용
    ///   (게임 자체가 친선 사격 감지에 이 인터페이스 사용)
    ///
    /// 진단 가치:
    /// - 우리 AoESafetyChecker 가 차단 못 한 케이스 가시화
    /// - 어떤 능력으로 어떤 아군이 피격됐는지 명확
    /// - 사용자 incident 보고 검증
    /// </summary>
    public class FriendlyFireDetector : IWarhammerAttackHandler, ISubscriber
    {
        private static FriendlyFireDetector _instance;
        private bool _isSubscribed = false;

        public static FriendlyFireDetector Instance => _instance ??= new FriendlyFireDetector();

        public void Subscribe()
        {
            if (_isSubscribed) return;
            EventBus.Subscribe(this);
            _isSubscribed = true;
            Log.Engine.Info("[FriendlyFireDetector] Subscribed");
        }

        public void Unsubscribe()
        {
            if (!_isSubscribed) return;
            EventBus.Unsubscribe(this);
            _isSubscribed = false;
            Log.Engine.Info("[FriendlyFireDetector] Unsubscribed");
        }

        /// <summary>
        /// 게임 attack 이벤트 핸들러 — 모든 무기 공격 결과를 받음.
        /// 친선 사격 (player party 동료가 다른 동료 hit) 만 필터링하여 로그.
        /// </summary>
        public void HandleAttack(RulePerformAttack rule)
        {
            try
            {
                if (rule == null) return;
                if (!rule.ResultIsHit) return;  // 빗나간 공격 무시

                var attacker = rule.Initiator as BaseUnitEntity;
                var target = rule.ConcreteTarget as BaseUnitEntity;
                if (attacker == null || target == null) return;

                // 동일 unit 자기 자신 hit 무시 (예: self-damage buff)
                if (attacker == target) return;

                // 둘 다 player party 인 경우만 친선 사격 (적이 아군 친선 사격은 우리 관심사 X)
                if (!attacker.IsInPlayerParty) return;
                if (!target.IsInPlayerParty) return;

                // ★ 친선 사격 감지 — 게임 IsAlly 호출
                if (!target.IsAlly(attacker)) return;

                string abilityName = rule.Ability?.Name ?? "(no ability)";
                string abilityGuid = rule.Ability?.Blueprint?.AssetGuid?.ToString() ?? "(no guid)";

                // v3.117.31: 추가 진단 — chain 메커니즘 추적용
                string weaponName = "(none)";
                try {
                    var w = attacker.Body?.PrimaryHand?.MaybeWeapon?.Blueprint;
                    weaponName = w?.name ?? "(none)";
                } catch { }

                Log.Analysis.Warn(
                    $"[FriendlyFire] DETECTED: {attacker.CharacterName} → {target.CharacterName} " +
                    $"with {abilityName} ({abilityGuid}) [equippedWeapon={weaponName}]");
            }
            catch (Exception ex)
            {
                // 진단 핸들러 — 어떤 예외도 게임 동작에 영향 없도록 격리
                Log.Engine.Warn($"[FriendlyFireDetector] HandleAttack failed: {ex.Message}");
            }
        }
    }
}
