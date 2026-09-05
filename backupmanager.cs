using Rage;
using PolicingRedefined.API;
using PolicingRedefined.Backup.Entities;

namespace BackupPlacement
{
    public static class BackupManager
    {
        public static bool RequestBackup(
            EBackupUnit unitType,
            EBackupResponseCode responseCode,
            Vector3 destination
        )
        {
            Game.LogTrivial(
                "[BackupPlacement] Requesting PR backup: " +
                $"Unit={unitType}, " +
                $"Code={responseCode}, " +
                $"Destination=(" +
                $"{destination.X:F2}, " +
                $"{destination.Y:F2}, " +
                $"{destination.Z:F2})"
            );

            bool result =
                BackupAPI.RequestBackup(
                    unitType,
                    responseCode,
                    destination
                );

            Game.LogTrivial(
                "[BackupPlacement] PR RequestBackup returned: " +
                result
            );

            return result;
        }
    }
}