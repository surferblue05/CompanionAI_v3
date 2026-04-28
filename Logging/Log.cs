using System;

namespace CompanionAI_v3.Logging
{
    /// <summary>
    /// ★ Phase 2: 카테고리 로깅 인프라.
    /// 기존 Main.Log* 평면 4함수 대체. 모듈별 필터링 + 디버깅 효율 향상.
    ///
    /// 매핑:
    ///   Main.Log         → Log.&lt;Cat&gt;.Info
    ///   Main.LogDebug    → Log.&lt;Cat&gt;.Debug   (EnableDebugLogging 게이팅 보존)
    ///   Main.LogWarning  → Log.&lt;Cat&gt;.Warn
    ///   Main.LogError    → Log.&lt;Cat&gt;.Error
    ///
    /// 카테고리 → 폴더:
    ///   Engine        → Core, Execution, GameInterface
    ///   Planning      → Planning (Planners, Plans, LLM)
    ///   Analysis      → Analysis
    ///   MachineSpirit → MachineSpirit (Knowledge)
    ///   Persistence   → Settings, Data
    ///   Diagnostics   → Diagnostics
    ///   UI            → UI
    ///
    /// 출력 형식: [CompanionAI][&lt;CategoryName&gt;][&lt;LEVEL&gt;] message
    /// </summary>
    public static class Log
    {
        public static readonly Category Engine        = new Category("Engine");
        public static readonly Category Planning      = new Category("Planning");
        public static readonly Category Analysis      = new Category("Analysis");
        public static readonly Category MachineSpirit = new Category("MachineSpirit");
        public static readonly Category Persistence   = new Category("Persistence");
        public static readonly Category Diagnostics   = new Category("Diagnostics");
        public static readonly Category UI            = new Category("UI");
    }

    /// <summary>
    /// 카테고리별 로거. ModEntry?.Logger?. null-safety 패턴 (기존 Main.Log* 와 동일).
    /// </summary>
    public sealed class Category
    {
        private readonly string _name;

        public Category(string name)
        {
            _name = name;
        }

        public void Info(string message)
        {
            Main.ModEntry?.Logger?.Log($"[CompanionAI][{_name}] {message}");
        }

        public void Debug(string message)
        {
            if (Settings.ModSettings.Instance?.EnableDebugLogging ?? false)
                Main.ModEntry?.Logger?.Log($"[CompanionAI][{_name}][DEBUG] {message}");
        }

        public void Warn(string message)
        {
            Main.ModEntry?.Logger?.Warning($"[CompanionAI][{_name}][WARN] {message}");
        }

        public void Error(string message)
        {
            Main.ModEntry?.Logger?.Error($"[CompanionAI][{_name}][ERROR] {message}");
        }

        public void Error(Exception ex, string message)
        {
            if (ex == null)
            {
                Main.ModEntry?.Logger?.Error($"[CompanionAI][{_name}][ERROR] {message}");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"[CompanionAI][{_name}][ERROR] {message}");
            sb.Append($" | {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine();
                sb.Append("  Stack: ");
                sb.Append(ex.StackTrace);
            }
            if (ex.InnerException != null)
            {
                sb.AppendLine();
                sb.Append($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Main.ModEntry?.Logger?.Error(sb.ToString());
        }
    }
}
