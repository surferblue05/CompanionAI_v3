using System;
using System.IO;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// v3.117.65: 영속화 파일 손상 복구 공통 헬퍼.
    /// PerSaveSettings / ModSettings / AIConfig 가 같은 corruption guard 패턴을 공유 (3곳 동일 코드 중복 → 추출).
    /// </summary>
    public static class PersistenceUtils
    {
        /// <summary>30일 지난 백업 자동 삭제 임계값.</summary>
        private const int BackupRetentionDays = 30;

        /// <summary>
        /// 손상 파일을 timestamp 접미사로 백업. 같은 초에 충돌 시 -1, -2 ... fallback.
        /// 추가로 같은 폴더의 BackupRetentionDays 일 지난 .corrupted-* 자동 삭제.
        /// </summary>
        public static void BackupCorruptedFile(string filePath, string logPrefix, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var basePath = $"{filePath}.corrupted-{stamp}";
                var backupPath = basePath;

                // v3.117.65: 같은 초 충돌 시 -1, -2 ... suffix. 최대 100 시도 (실용적 한계).
                for (int i = 1; File.Exists(backupPath) && i < 100; i++)
                    backupPath = $"{basePath}-{i}";

                if (File.Exists(backupPath))
                {
                    Log.Persistence.Error($"{logPrefix} Backup gave up after 100 collisions for {Path.GetFileName(filePath)}. Aborting backup, save will still be blocked.");
                    return;
                }

                File.Copy(filePath, backupPath, overwrite: false);
                Log.Persistence.Warn($"{logPrefix} Corrupted file backed up: {Path.GetFileName(backupPath)} (reason: {reason})");

                PruneStaleBackups(filePath, logPrefix);
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"{logPrefix} Backup failed for {filePath}: {ex.Message} ({ex.GetType().Name})");
            }
        }

        /// <summary>
        /// 같은 폴더에 `{file}.corrupted-*` 백업 존재 여부.
        /// Load 진입 시 호출하여 손상 marker 영구화 — static `_loadFailed` flag 가 mod 재로드로 휘발되는 문제 방지.
        /// </summary>
        public static bool HasCorruptedBackup(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath)) return false;
                var dir = Path.GetDirectoryName(filePath);
                var pattern = Path.GetFileName(filePath) + ".corrupted-*";
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return Directory.EnumerateFiles(dir, pattern).GetEnumerator().MoveNext();
            }
            catch
            {
                // 권한/경로 에러 시 safe default = false (false negative 가 false positive 보다 안전 —
                // Save 차단 false positive 는 사용자 데이터 손실 = 더 큰 비용)
                return false;
            }
        }

        /// <summary>BackupRetentionDays 일 지난 .corrupted-* 자동 삭제. 무제한 누적 방지.</summary>
        private static void PruneStaleBackups(string filePath, string logPrefix)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                var pattern = Path.GetFileName(filePath) + ".corrupted-*";
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                var cutoff = DateTime.Now.AddDays(-BackupRetentionDays);
                int pruned = 0;
                foreach (var bak in Directory.EnumerateFiles(dir, pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTime(bak) < cutoff)
                        {
                            File.Delete(bak);
                            pruned++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Persistence.Debug($"{logPrefix} Prune skip {Path.GetFileName(bak)}: {ex.Message}");
                    }
                }
                if (pruned > 0) Log.Persistence.Debug($"{logPrefix} Pruned {pruned} stale backups (>{BackupRetentionDays}d)");
            }
            catch (Exception ex)
            {
                Log.Persistence.Debug($"{logPrefix} Prune scan failed: {ex.Message}");
            }
        }
    }
}
