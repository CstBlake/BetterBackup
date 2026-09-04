using Rage;
using LSPD_First_Response.Mod.API;

namespace BackupPlacement
{
    public class EntryPoint : Plugin
    {
        private static PlacementController _placementController;
        private static BackupWatcher _backupWatcher;

        public override void Initialize()
        {
            Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;

            Game.LogTrivial(
                "[BetterBackup] Plugin initialized."
            );
        }

        public override void Finally()
        {
            Functions.OnOnDutyStateChanged -= OnOnDutyStateChanged;

            _backupWatcher?.Stop();
            _placementController?.Stop();

            Game.LogTrivial(
                "[BetterBackup] Plugin unloaded."
            );
        }

        private static void OnOnDutyStateChanged(bool onDuty)
        {
            if (!onDuty)
            {
                _backupWatcher?.Stop();
                _placementController?.Stop();

                _backupWatcher = null;
                _placementController = null;

                return;
            }

            Game.LogTrivial(
                "[BetterBackup] Player went on duty."
            );

            BetterBackupConfig.Load();

            _placementController =
                new PlacementController();

            _placementController.Start();

            _backupWatcher =
                new BackupWatcher(
                    _placementController
                );

            _backupWatcher.Start();

            Game.DisplayNotification(
                "~b~BetterBackup~s~ loaded.<br>" +
                "Use Policing Redefined backup normally."
            );
        }
    }
}