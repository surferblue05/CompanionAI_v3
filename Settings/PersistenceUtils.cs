using System;
using System.IO;
using System.Linq;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// 영속화 파일 손상 복구 공통 헬퍼.
    /// PerSaveSettings / ModSettings / AIConfig 가 같은 corruption guard 패턴을 공유.
    /// .corrupted-* 백업 파일이 곧 "Save 차단" marker — 디스크에 있어 mod 재로드에도 영속,
    /// 사용자가 복구 후 백업을 삭제하면 차단 즉시 해제.
    /// </summary>
    public static class PersistenceUtils
    {
        /// <summary>
        /// 손상 파일을 timestamp 접미사로 백업.
        /// 기존 .corrupted-* 가 이미 있으면 skip — Save 가 차단된 동안 원본은 변하지 않으므로
        /// 매 실행마다 같은 내용을 다시 백업할 필요 없음 (누적 방지, marker 도 이미 존재).
        /// </summary>
        public static void BackupCorruptedFile(string filePath, string logPrefix, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                if (HasCorruptedBackup(filePath))
                {
                    Log.Persistence.Debug($"{logPrefix} Backup skipped — existing .corrupted-* already present (reason: {reason})");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backupPath = $"{filePath}.corrupted-{stamp}";

                File.Copy(filePath, backupPath, overwrite: false);
                Log.Persistence.Warn($"{logPrefix} Corrupted file backed up: {Path.GetFileName(backupPath)} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"{logPrefix} Backup failed for {filePath}: {ex.Message} ({ex.GetType().Name})");
            }
        }

        /// <summary>
        /// 같은 폴더에 `{file}.corrupted-*` 백업 존재 여부 = Save 차단 marker.
        /// Save 진입 시 호출 — static flag 가 mod 재로드로 휘발되어도 디스크 marker 로 차단 유지.
        /// </summary>
        public static bool HasCorruptedBackup(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath)) return false;
                var dir = Path.GetDirectoryName(filePath);
                var pattern = Path.GetFileName(filePath) + ".corrupted-*";
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return Directory.EnumerateFiles(dir, pattern).Any();
            }
            catch
            {
                // 권한/경로 에러 시 safe default = false (false negative 가 false positive 보다 안전 —
                // Save 차단 false positive 는 사용자 데이터 손실 = 더 큰 비용)
                return false;
            }
        }
    }
}
