using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using PolicingRedefined.API;

namespace BackupPlacement
{
    public class PlacementController
    {
        private bool _running;
        private bool _placementActive;

        private Vector3 _markerPosition;
        private float _markerHeading;

        private Vehicle _activeBackupVehicle;
        private EBackupUnit? _activeBackupUnitType;

        /*
         * Player's remembered police/emergency vehicle.
         */
        private Vehicle _playerPoliceVehicle;

        private readonly List<Vector3> _circlePoints =
            new List<Vector3>();

        private Vector3 _circleCenter;

        private Vector3 _arrowLeft;
        private Vector3 _arrowRight;
        private Vector3 _arrowTip;

        private Vector3 _lastSampledPosition;
        private float _lastSampledHeading;

        private bool _geometryValid;

        private const float RotationStep =
            5f;

        private const int CircleSegments =
            16;

        private const float CircleRadius =
            2.25f;

        private const float GroundTraceUp =
            6f;

        private const float GroundTraceDown =
            8f;

        private const float GeometryMoveThreshold =
            0.05f;

        /*
         * Actual parking distance is calculated from the model
         * dimensions of both vehicles.
         *
         * This is only the physical bumper clearance.
         */
        private const float BumperClearance =
            1.25f;

        private const float FallbackHalfLength =
            2.5f;

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running =
                true;

            GameFiber.StartNew(
                MainLoop,
                "BackupPlacement.MainLoop"
            );

            Game.LogTrivial(
                "[BackupPlacement] PlacementController started."
            );
        }

        public void Stop()
        {
            _running =
                false;

            _placementActive =
                false;

            _activeBackupVehicle =
                null;

            _activeBackupUnitType =
                null;

            _playerPoliceVehicle =
                null;

            Game.LogTrivial(
                "[BackupPlacement] PlacementController stopped."
            );
        }

        private void MainLoop()
        {
            while (_running)
            {
                GameFiber.Yield();

                try
                {
                    UpdatePlayerPoliceVehicle();

                    if (_placementActive)
                    {
                        UpdatePlacement();
                    }
                }
                catch (Exception ex)
                {
                    Game.LogTrivial(
                        "[BackupPlacement] Placement ERROR: " +
                        ex
                    );

                    _placementActive =
                        false;

                    _activeBackupVehicle =
                        null;

                    _activeBackupUnitType =
                        null;

                    Game.DisplayNotification(
                        "~r~Backup Placement error.~s~ " +
                        "Placement mode cancelled."
                    );
                }
            }
        }

        // =============================================================
        // PLAYER POLICE VEHICLE TRACKING
        // =============================================================

        private void UpdatePlayerPoliceVehicle()
        {
            Ped player =
                Game.LocalPlayer.Character;

            if (player == null ||
                !player.Exists())
            {
                return;
            }

            Vehicle currentVehicle =
                player.CurrentVehicle;

            if (currentVehicle == null ||
                !currentVehicle.Exists())
            {
                /*
                 * Keep the previously remembered cruiser while
                 * the player is on foot.
                 */
                return;
            }

            /*
             * Civilian vehicles do not replace the remembered
             * police cruiser.
             */
            if (!IsQualifyingPoliceVehicle(
                currentVehicle))
            {
                return;
            }

            if (_playerPoliceVehicle == null ||
                !_playerPoliceVehicle.Exists() ||
                _playerPoliceVehicle.Handle !=
                    currentVehicle.Handle)
            {
                _playerPoliceVehicle =
                    currentVehicle;

                Game.LogTrivial(
                    "[BackupPlacement] Player police vehicle tracked. " +
                    $"Handle={currentVehicle.Handle}, " +
                    $"Model={currentVehicle.Model.Name}"
                );
            }
        }

        private static bool IsQualifyingPoliceVehicle(
            Vehicle vehicle
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return false;
            }

            try
            {
                return vehicle.HasSiren;
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BackupPlacement] Police vehicle check failed " +
                    $"for {vehicle.Model.Name}: {ex.Message}"
                );

                return false;
            }
        }

        // =============================================================
        // BACKUP PLACEMENT START
        // =============================================================

        public void BeginForBackup(
            Vehicle vehicle,
            EBackupUnit? unitType
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                Game.LogTrivial(
                    "[BackupPlacement] BeginForBackup received " +
                    "an invalid vehicle."
                );

                return;
            }

            _activeBackupVehicle =
                vehicle;

            _activeBackupUnitType =
                unitType;

            BeginPlacement();

            Game.LogTrivial(
                "[BackupPlacement] Placement linked to vehicle " +
                $"Handle={vehicle.Handle}, " +
                $"Model={vehicle.Model.Name}, " +
                $"UnitType=" +
                $"{(_activeBackupUnitType.HasValue
                    ? _activeBackupUnitType.Value.ToString()
                    : "Unknown")}"
            );
        }

        private void BeginPlacement()
        {
            _placementActive =
                true;

            Ped player =
                Game.LocalPlayer.Character;

            if (player == null ||
                !player.Exists())
            {
                Game.LogTrivial(
                    "[BackupPlacement] Cannot start placement: " +
                    "player does not exist."
                );

                _placementActive =
                    false;

                return;
            }

            _markerPosition =
                player.Position;

            _markerHeading =
                player.Heading;

            _geometryValid =
                false;

            string unitText =
                _activeBackupUnitType.HasValue
                    ? _activeBackupUnitType.Value.ToString()
                    : "Backup Unit";

            string rotateLeftText =
                BetterBackupConfig.GetDisplayName(
                    BetterBackupConfig.RotateLeftKey
                );

            string rotateRightText =
                BetterBackupConfig.GetDisplayName(
                    BetterBackupConfig.RotateRightKey
                );

            string confirmText =
                BetterBackupConfig.GetDisplayName(
                    BetterBackupConfig.PlacementConfirmKey
                );

            string cancelText =
                BetterBackupConfig.GetDisplayName(
                    BetterBackupConfig.PlacementCancelKey
                );

            string parkBehindText =
                BetterBackupConfig.GetDisplayName(
                    BetterBackupConfig.ParkBehindCruiserKey
                );

            Game.DisplayNotification(
                "~b~BACKUP PLACEMENT~s~<br>" +
                $"Position: ~y~{unitText}~s~<br>" +
                $"~y~{rotateLeftText}~s~ / " +
                $"~y~{rotateRightText}~s~ Rotate.<br>" +
                $"~g~{confirmText}~s~ Confirm    " +
                $"~r~{cancelText}~s~ Default response<br>" +
                $"~b~{parkBehindText}~s~ Park behind cruiser"
            );

            Game.LogTrivial(
                "[BackupPlacement] Placement mode started."
            );
        }

        // =============================================================
        // NORMAL PLAYER-FOLLOWING PLACEMENT
        // =============================================================

        private void UpdatePlacement()
        {
            /*
             * If PR dismisses the response while the placement UI
             * is still open, close BetterBackup's placement mode.
             */
            if (_activeBackupVehicle != null &&
                _activeBackupVehicle.Exists() &&
                PRTaskBridge.IsResponseCancelled(
                    _activeBackupVehicle))
            {
                Game.LogTrivial(
                    "[BackupPlacement] PR dismissed backup while " +
                    "placement UI was active."
                );

                _placementActive =
                    false;

                _activeBackupVehicle =
                    null;

                _activeBackupUnitType =
                    null;

                return;
            }

            UpdateMarkerPosition();
            UpdateRotation();

            UpdateGroundGeometryIfNeeded();

            DrawPlacementMarker();

            if (Game.IsKeyDown(
                BetterBackupConfig.ParkBehindCruiserKey))
            {
                ParkBehindCruiser();

                GameFiber.Sleep(250);

                return;
            }

            if (Game.IsKeyDown(
                BetterBackupConfig.PlacementConfirmKey))
            {
                ConfirmPlacement();

                GameFiber.Sleep(250);

                return;
            }

            if (Game.IsKeyDown(
                BetterBackupConfig.PlacementCancelKey))
            {
                CancelPlacement();

                GameFiber.Sleep(250);
            }
        }

        private void UpdateMarkerPosition()
        {
            Ped player =
                Game.LocalPlayer.Character;

            if (player == null ||
                !player.Exists())
            {
                return;
            }

            _markerPosition =
                player.Position;
        }

        private void UpdateRotation()
        {
            if (Game.IsKeyDown(
                BetterBackupConfig.RotateLeftKey))
            {
                _markerHeading +=
                    RotationStep;

                _markerHeading =
                    NormalizeHeading(
                        _markerHeading
                    );

                _geometryValid =
                    false;

                GameFiber.Sleep(80);
            }

            if (Game.IsKeyDown(
                BetterBackupConfig.RotateRightKey))
            {
                _markerHeading -=
                    RotationStep;

                _markerHeading =
                    NormalizeHeading(
                        _markerHeading
                    );

                _geometryValid =
                    false;

                GameFiber.Sleep(80);
            }
        }

        // =============================================================
        // PARK BEHIND CRUISER
        //
        // IMPORTANT:
        //
        // Pressing NUM 5 immediately exits visual placement mode.
        // NO marker is drawn behind the cruiser.
        // =============================================================

        private void ParkBehindCruiser()
        {
            if (_activeBackupVehicle == null ||
                !_activeBackupVehicle.Exists())
            {
                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "~r~Backup vehicle is no longer available.~s~"
                );

                return;
            }

            /*
             * PR cancellation always wins.
             */
            if (PRTaskBridge.IsResponseCancelled(
                _activeBackupVehicle))
            {
                Game.LogTrivial(
                    "[BackupPlacement] ParkBehindCruiser ignored: " +
                    "PR is dismissing the responding unit."
                );

                _placementActive =
                    false;

                _activeBackupVehicle =
                    null;

                _activeBackupUnitType =
                    null;

                return;
            }

            UpdatePlayerPoliceVehicle();

            if (_playerPoliceVehicle == null ||
                !_playerPoliceVehicle.Exists())
            {
                Game.LogTrivial(
                    "[BackupPlacement] ParkBehindCruiser failed: " +
                    "no tracked player police vehicle."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "~r~No police cruiser has been identified.~s~"
                );

                return;
            }

            Vehicle cruiser =
                _playerPoliceVehicle;

            Vehicle backupVehicle =
                _activeBackupVehicle;

            if (cruiser.Handle ==
                backupVehicle.Handle)
            {
                Game.LogTrivial(
                    "[BackupPlacement] ParkBehindCruiser failed: " +
                    "tracked cruiser is the responding backup vehicle."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "~r~Police cruiser could not be identified.~s~"
                );

                return;
            }

            Vector3 targetPosition;
            float targetHeading;

            if (!TryCalculateBehindCruiserTarget(
                cruiser,
                backupVehicle,
                out targetPosition,
                out targetHeading))
            {
                Game.LogTrivial(
                    "[BackupPlacement] ParkBehindCruiser failed: " +
                    "could not calculate target."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "~r~Could not calculate parking position.~s~"
                );

                return;
            }

            /*
             * Kill visual placement immediately.
             *
             * The player-following circle therefore disappears
             * on the next frame and NOTHING replaces it.
             */
            _placementActive =
                false;

            _geometryValid =
                false;

            Game.LogTrivial(
                "[BackupPlacement] Park behind cruiser selected. " +
                $"Cruiser={cruiser.Model.Name} / {cruiser.Handle}, " +
                $"Backup={backupVehicle.Model.Name} / {backupVehicle.Handle}, " +
                $"Target=(" +
                $"{targetPosition.X:F2}, " +
                $"{targetPosition.Y:F2}, " +
                $"{targetPosition.Z:F2}), " +
                $"Heading={targetHeading:F1}"
            );

            /*
             * Same existing final-approach machinery.
             *
             * No special driving.
             * No special teleport.
             * No marker tracking.
             */
            FinalApproachController.Start(
                backupVehicle,
                targetPosition,
                targetHeading
            );

            Game.DisplayNotification(
                "~b~BACKUP PLACEMENT~s~<br>" +
                "~g~Park behind cruiser selected.~s~"
            );

            _activeBackupVehicle =
                null;

            _activeBackupUnitType =
                null;
        }

        private bool TryCalculateBehindCruiserTarget(
            Vehicle cruiser,
            Vehicle backupVehicle,
            out Vector3 targetPosition,
            out float targetHeading
        )
        {
            targetPosition =
                Vector3.Zero;

            targetHeading =
                0f;

            if (cruiser == null ||
                !cruiser.Exists() ||
                backupVehicle == null ||
                !backupVehicle.Exists())
            {
                return false;
            }

            targetHeading =
                NormalizeHeading(
                    cruiser.Heading
                );

            float cruiserRearExtent;
            float backupFrontExtent;

            GetVehicleLongitudinalExtents(
                cruiser,
                out cruiserRearExtent,
                out _
            );

            GetVehicleLongitudinalExtents(
                backupVehicle,
                out _,
                out backupFrontExtent
            );

            float rearwardDistance =
                cruiserRearExtent +
                BumperClearance +
                backupFrontExtent;

            /*
             * Vehicle-local negative Y is directly behind
             * the tracked cruiser.
             */
            Vector3 requestedPosition =
                cruiser.GetOffsetPosition(
                    new Vector3(
                        0f,
                        -rearwardDistance,
                        0f
                    )
                );

            /*
             * Independently ground-trace the destination so we're
             * not simply inheriting the cruiser's Z.
             */
            Vector3 groundedPosition;

            if (TryGetGroundPoint(
                requestedPosition,
                out groundedPosition))
            {
                targetPosition =
                    groundedPosition;
            }
            else
            {
                targetPosition =
                    requestedPosition;
            }

            return true;
        }

        private static void GetVehicleLongitudinalExtents(
            Vehicle vehicle,
            out float rearExtent,
            out float frontExtent
        )
        {
            rearExtent =
                FallbackHalfLength;

            frontExtent =
                FallbackHalfLength;

            if (vehicle == null ||
                !vehicle.Exists())
            {
                return;
            }

            try
            {
                Vector3 minimum;
                Vector3 maximum;

                NativeFunction.Natives.GET_MODEL_DIMENSIONS(
                    vehicle.Model.Hash,
                    out minimum,
                    out maximum
                );

                float calculatedRear =
                    Math.Abs(
                        minimum.Y
                    );

                float calculatedFront =
                    Math.Abs(
                        maximum.Y
                    );

                if (calculatedRear > 0.25f &&
                    calculatedRear < 10.0f)
                {
                    rearExtent =
                        calculatedRear;
                }

                if (calculatedFront > 0.25f &&
                    calculatedFront < 10.0f)
                {
                    frontExtent =
                        calculatedFront;
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BackupPlacement] Could not obtain vehicle dimensions " +
                    $"for {vehicle.Model.Name}: {ex.Message}"
                );
            }
        }

        // =============================================================
        // NORMAL MANUAL MARKER GEOMETRY
        // =============================================================

        private void UpdateGroundGeometryIfNeeded()
        {
            float movement =
                Distance(
                    _markerPosition,
                    _lastSampledPosition
                );

            float headingDifference =
                Math.Abs(
                    NormalizeAngleDifference(
                        _markerHeading -
                        _lastSampledHeading
                    )
                );

            if (_geometryValid &&
                movement <
                    GeometryMoveThreshold &&
                headingDifference <
                    0.1f)
            {
                return;
            }

            RebuildCircleGeometry();
            RebuildArrowGeometry();

            _lastSampledPosition =
                _markerPosition;

            _lastSampledHeading =
                _markerHeading;

            _geometryValid =
                true;
        }

        private void RebuildCircleGeometry()
        {
            _circlePoints.Clear();

            if (!TryGetGroundPoint(
                _markerPosition,
                out _circleCenter))
            {
                _circleCenter =
                    _markerPosition;
            }

            _circleCenter.Z +=
                0.035f;

            for (int i = 0;
                 i < CircleSegments;
                 i++)
            {
                float angle =
                    ((float)Math.PI *
                    2f *
                    i) /
                    CircleSegments;

                float x =
                    _markerPosition.X +
                    (
                        (float)Math.Cos(angle) *
                        CircleRadius
                    );

                float y =
                    _markerPosition.Y +
                    (
                        (float)Math.Sin(angle) *
                        CircleRadius
                    );

                Vector3 requestedPoint =
                    new Vector3(
                        x,
                        y,
                        _markerPosition.Z
                    );

                Vector3 groundedPoint;

                if (TryGetGroundPoint(
                    requestedPoint,
                    out groundedPoint))
                {
                    groundedPoint.Z +=
                        0.04f;
                }
                else
                {
                    groundedPoint =
                        requestedPoint;
                }

                _circlePoints.Add(
                    groundedPoint
                );
            }
        }

        private void RebuildArrowGeometry()
        {
            Vector3 forward =
                HeadingToDirection(
                    _markerHeading
                );

            Vector3 right =
                new Vector3(
                    forward.Y,
                    -forward.X,
                    0f
                );

            Vector3 arrowBase =
                _markerPosition +
                (
                    forward *
                    CircleRadius
                );

            Vector3 requestedLeft =
                arrowBase -
                (right * 0.75f);

            Vector3 requestedRight =
                arrowBase +
                (right * 0.75f);

            Vector3 requestedTip =
                _markerPosition +
                (
                    forward *
                    (CircleRadius + 1.5f)
                );

            if (!TryGetGroundPoint(
                requestedLeft,
                out _arrowLeft))
            {
                _arrowLeft =
                    requestedLeft;
            }

            if (!TryGetGroundPoint(
                requestedRight,
                out _arrowRight))
            {
                _arrowRight =
                    requestedRight;
            }

            if (!TryGetGroundPoint(
                requestedTip,
                out _arrowTip))
            {
                _arrowTip =
                    requestedTip;
            }

            _arrowLeft.Z +=
                0.10f;

            _arrowRight.Z +=
                0.10f;

            _arrowTip.Z +=
                0.10f;
        }

        private bool TryGetGroundPoint(
            Vector3 position,
            out Vector3 groundedPosition
        )
        {
            Vector3 start =
                new Vector3(
                    position.X,
                    position.Y,
                    position.Z +
                    GroundTraceUp
                );

            Vector3 end =
                new Vector3(
                    position.X,
                    position.Y,
                    position.Z -
                    GroundTraceDown
                );

            HitResult hit =
                World.TraceLine(
                    start,
                    end,
                    TraceFlags.IntersectWorld
                );

            if (hit.Hit)
            {
                groundedPosition =
                    hit.HitPosition;

                return true;
            }

            groundedPosition =
                position;

            return false;
        }

        private void DrawPlacementMarker()
        {
            DrawFilledCircle();
            DrawFilledArrow();
        }

        private void DrawFilledCircle()
        {
            if (_circlePoints.Count <
                CircleSegments)
            {
                return;
            }

            for (int i = 0;
                 i < _circlePoints.Count;
                 i++)
            {
                int next =
                    (i + 1) %
                    _circlePoints.Count;

                DrawPolyBothSides(
                    _circleCenter,
                    _circlePoints[i],
                    _circlePoints[next],

                    0,
                    110,
                    255,
                    65
                );
            }

            for (int i = 0;
                 i < _circlePoints.Count;
                 i++)
            {
                int next =
                    (i + 1) %
                    _circlePoints.Count;

                DrawLine(
                    _circlePoints[i],
                    _circlePoints[next],

                    0,
                    140,
                    255,
                    220
                );
            }
        }

        private void DrawFilledArrow()
        {
            DrawPolyBothSides(
                _arrowLeft,
                _arrowRight,
                _arrowTip,

                255,
                220,
                0,
                220
            );

            DrawLine(
                _arrowLeft,
                _arrowTip,

                255,
                230,
                0,
                255
            );

            DrawLine(
                _arrowRight,
                _arrowTip,

                255,
                230,
                0,
                255
            );

            DrawLine(
                _arrowLeft,
                _arrowRight,

                255,
                230,
                0,
                255
            );
        }

        private static void DrawPolyBothSides(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            int red,
            int green,
            int blue,
            int alpha
        )
        {
            NativeFunction.Natives.DRAW_POLY(
                a.X,
                a.Y,
                a.Z,

                b.X,
                b.Y,
                b.Z,

                c.X,
                c.Y,
                c.Z,

                red,
                green,
                blue,
                alpha
            );

            NativeFunction.Natives.DRAW_POLY(
                c.X,
                c.Y,
                c.Z,

                b.X,
                b.Y,
                b.Z,

                a.X,
                a.Y,
                a.Z,

                red,
                green,
                blue,
                alpha
            );
        }

        private static void DrawLine(
            Vector3 a,
            Vector3 b,
            int red,
            int green,
            int blue,
            int alpha
        )
        {
            NativeFunction.Natives.DRAW_LINE(
                a.X,
                a.Y,
                a.Z,

                b.X,
                b.Y,
                b.Z,

                red,
                green,
                blue,
                alpha
            );
        }

        // =============================================================
        // MANUAL CONFIRM
        // =============================================================

        private void ConfirmPlacement()
        {
            if (_activeBackupVehicle != null &&
                _activeBackupVehicle.Exists() &&
                PRTaskBridge.IsResponseCancelled(
                    _activeBackupVehicle))
            {
                Game.LogTrivial(
                    "[BackupPlacement] ConfirmPlacement ignored: " +
                    "PR is dismissing the responding unit."
                );

                _placementActive =
                    false;

                _activeBackupVehicle =
                    null;

                _activeBackupUnitType =
                    null;

                return;
            }

            _placementActive =
                false;

            Vector3 confirmedPosition =
                _markerPosition;

            float confirmedHeading =
                _markerHeading;

            if (_activeBackupVehicle == null ||
                !_activeBackupVehicle.Exists())
            {
                Game.LogTrivial(
                    "[BackupPlacement] ConfirmPlacement failed: " +
                    "captured backup vehicle no longer exists."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "~r~Backup vehicle is no longer available.~s~"
                );

                _activeBackupVehicle =
                    null;

                _activeBackupUnitType =
                    null;

                return;
            }

            Vehicle confirmedVehicle =
                _activeBackupVehicle;

            Game.LogTrivial(
                "[BackupPlacement] Confirmed placement: " +
                $"Vehicle={confirmedVehicle.Model.Name} / " +
                $"{confirmedVehicle.Handle}, " +
                $"X={confirmedPosition.X:F2}, " +
                $"Y={confirmedPosition.Y:F2}, " +
                $"Z={confirmedPosition.Z:F2}, " +
                $"Heading={confirmedHeading:F1}"
            );

            FinalApproachController.Start(
                confirmedVehicle,
                confirmedPosition,
                confirmedHeading
            );

            Game.DisplayNotification(
                "~b~BACKUP PLACEMENT~s~<br>" +
                "~g~Position confirmed.~s~<br>" +
                "Unit will park at selected position."
            );

            _activeBackupVehicle =
                null;

            _activeBackupUnitType =
                null;
        }

        // =============================================================
        // E CANCEL / DEFAULT RESPONSE
        // =============================================================

        private void CancelPlacement()
        {
            _placementActive =
                false;

            Game.LogTrivial(
                "[BackupPlacement] Placement cancelled. " +
                "PR vehicle left untouched."
            );

            Game.DisplayNotification(
                "~b~BACKUP PLACEMENT~s~<br>" +
                "Placement cancelled.<br>" +
                "Unit will respond normally."
            );

            _activeBackupVehicle =
                null;

            _activeBackupUnitType =
                null;
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private static Vector3 HeadingToDirection(
            float heading
        )
        {
            float radians =
                heading *
                ((float)Math.PI / 180f);

            return new Vector3(
                -(float)Math.Sin(radians),
                (float)Math.Cos(radians),
                0f
            );
        }

        private static float Distance(
            Vector3 a,
            Vector3 b
        )
        {
            float x =
                a.X - b.X;

            float y =
                a.Y - b.Y;

            float z =
                a.Z - b.Z;

            return (float)Math.Sqrt(
                (x * x) +
                (y * y) +
                (z * z)
            );
        }

        private static float NormalizeHeading(
            float heading
        )
        {
            while (heading >= 360f)
            {
                heading -= 360f;
            }

            while (heading < 0f)
            {
                heading += 360f;
            }

            return heading;
        }

        private static float NormalizeAngleDifference(
            float angle
        )
        {
            while (angle > 180f)
            {
                angle -= 360f;
            }

            while (angle < -180f)
            {
                angle += 360f;
            }

            return angle;
        }
    }
}