using System;
using System.IO;
using Newtonsoft.Json;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// ★ v3.20.0: 사용자 노출 AOE 설정
    /// - MaxPlayerAlliesHit : 아군 최대 피격 허용 수 (0=아군 피격 완전 차단)
    /// - MinClusterSize     : AoE 클러스터 최소 적 수 (1=단일 적도 AoE 허용)
    /// - SelfAoeMinAdjacentEnemies : Self-AoE 최소 인접 적 수
    ///
    /// 내부 AoE 가중치(EnemyHitScore, PenaltyMultiplier 등)는 SC.cs 참조
    /// </summary>
    [Serializable]
    public class AoEConfig
    {
        [JsonProperty("maxPlayerAlliesHit")]
        public int MaxPlayerAlliesHit { get; set; } = 0;

        [JsonProperty("minClusterSize")]
        public int MinClusterSize { get; set; } = 2;

        [JsonProperty("selfAoeMinAdjacentEnemies")]
        public int SelfAoeMinAdjacentEnemies { get; set; } = 1;
    }

    /// <summary>
    /// ★ v3.20.0: 사용자 노출 무기 로테이션 설정
    /// - MaxSwitchesPerTurn : 턴당 최대 무기 전환 횟수
    ///
    /// 내부 상수(MinEnemiesForAlternateAoE 등)는 SC.cs 참조
    /// </summary>
    [Serializable]
    public class WeaponRotationConfig
    {
        [JsonProperty("maxSwitchesPerTurn")]
        public int MaxSwitchesPerTurn { get; set; } = 2;
    }

    /// <summary>
    /// ★ v3.20.0: 사용자 영구 설정 (aiconfig.json)
    ///
    /// 설계 원칙:
    ///   - 여기 있는 값은 사용자가 의도적으로 바꾸는 것 → 업데이트 후에도 유지되어야 함
    ///   - 개발자 튜닝 상수(스코어링 가중치, 임계값 등)는 SC.cs로 이동 → 항상 최신값 적용
    /// </summary>
    [Serializable]
    public class AIConfig
    {
        [JsonProperty("aoe")]
        public AoEConfig AoE { get; set; } = new AoEConfig();

        [JsonProperty("weaponRotation")]
        public WeaponRotationConfig WeaponRotation { get; set; } = new WeaponRotationConfig();

        public static AIConfig Instance { get; private set; }

        private static string _modPath;

        // v3.117.60: Load 실패 시 Save 차단 — 빈 설정이 원본 덮어쓰는 것 방지.
        private static bool _loadFailed = false;

        public static AIConfig CreateDefault()
        {
            return new AIConfig
            {
                AoE = new AoEConfig(),
                WeaponRotation = new WeaponRotationConfig()
            };
        }

        public static void Load(string modPath)
        {
            _modPath = modPath;
            string configPath = Path.Combine(modPath, "aiconfig.json");
            bool fileExisted = File.Exists(configPath);

            // v3.117.65: 같은 폴더에 .corrupted-* 가 이미 존재하면 손상 marker 영구화.
            if (fileExisted && PersistenceUtils.HasCorruptedBackup(configPath))
            {
                _loadFailed = true;
                Log.Persistence.Warn("[AIConfig] Existing aiconfig.json.corrupted-* backup detected — Save remains blocked until user recovers manually.");
            }

            try
            {
                if (fileExisted)
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<AIConfig>(json);

                    if (config != null)
                    {
                        Instance = config;
                        // 정상 로드 — 단 .corrupted-* 가 남아있으면 _loadFailed 는 위에서 set 된 상태 유지.

                        // null 보호 (구버전 JSON 호환 또는 필드 누락 시)
                        if (Instance.AoE == null) Instance.AoE = new AoEConfig();
                        if (Instance.WeaponRotation == null) Instance.WeaponRotation = new WeaponRotationConfig();

                        if (!_loadFailed) Log.Persistence.Info($"[AIConfig] Loaded from {configPath}");
                        else Log.Persistence.Info($"[AIConfig] Loaded from {configPath} — Save blocked (corruption backup exists)");
                        return;
                    }
                    // v3.117.60: deserialize 결과 null → corruption 처리.
                    PersistenceUtils.BackupCorruptedFile(configPath, "[AIConfig]", "deserialize returned null");
                    _loadFailed = true;
                    Log.Persistence.Warn("[AIConfig] aiconfig.json deserialize returned null → 빈 설정 사용, Save 차단됨.");
                    Instance = CreateDefault();
                    return;
                }
            }
            catch (Exception ex)
            {
                // v3.117.60: 손상 파일 백업 + Save 차단.
                PersistenceUtils.BackupCorruptedFile(configPath, "[AIConfig]", ex.Message);
                _loadFailed = true;
                Log.Persistence.Error($"[AIConfig] Failed to load: {ex.Message}. Save 차단 활성화 — .corrupted-* 백업 확인 필요.");
                Instance = CreateDefault();
                return;
            }

            // 파일 자체가 없는 경우만 default 생성 + Save (정상 첫 실행)
            Instance = CreateDefault();
            Save();
            Log.Persistence.Info("[AIConfig] Created default aiconfig.json");
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_modPath))
            {
                Log.Persistence.Error("[AIConfig] Cannot save - modPath not set");
                return;
            }

            // v3.117.60: Load 실패 후 미해결 상태에서는 Save 차단.
            if (_loadFailed)
            {
                Log.Persistence.Warn("[AIConfig] Save blocked — load 실패 후 미해결. .corrupted-* 백업 확인 후 수동 복구 필요.");
                return;
            }

            string configPath = Path.Combine(_modPath, "aiconfig.json");

            try
            {
                if (Instance == null) Instance = CreateDefault();

                // v3.117.60: read-only 사전 체크.
                if (File.Exists(configPath))
                {
                    var attrs = File.GetAttributes(configPath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        Log.Persistence.Error($"[AIConfig] aiconfig.json 가 읽기 전용입니다. " +
                            "OneDrive/안티바이러스/백업 도구 또는 수동 속성 변경 확인 필요. 설정 변경 사항이 저장되지 않습니다.");
                        return;
                    }
                }

                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(configPath, json);
                Log.Persistence.Debug("[AIConfig] Settings saved to aiconfig.json");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"[AIConfig] Failed to save: {ex.Message} ({ex.GetType().Name})");
            }
        }

        /// <summary>AOE 설정 (null-safe)</summary>
        public static AoEConfig GetAoEConfig()
        {
            return Instance?.AoE ?? new AoEConfig();
        }

        /// <summary>무기 로테이션 설정 (null-safe)</summary>
        public static WeaponRotationConfig GetWeaponRotationConfig()
        {
            return Instance?.WeaponRotation ?? new WeaponRotationConfig();
        }
    }
}
