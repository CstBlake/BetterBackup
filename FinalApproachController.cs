using System;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace BackupPlacement
{
    public static class FinalApproachController
    {
        private const float TakeoverDistance =
            30.0f;

        /*
         * If BetterBackup itself gets this close before PR
         * completes RespondToPosition, we complete the arrival.
         */
        private const float ArrivalDistance =
            2.5f;

        private const float FinalApproachSpeed =
            6.0f;

        private const float FinalDriveAcceptedDistance =
            0.75f;

        /*
         * Keep reasserting our final driving task because PR may
         * still try to influence the vehicle during RespondToPosition.
         */
        private const int DriveTaskRefreshMilliseconds =
            250;

        public static void Start(
            Vehicle vehicle,
            Vector3 destination,
            float heading
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                Game.LogTrivial(
                    "[BetterBackup] FinalApproachController.Start: invalid vehicle."
                );

                return;
            }

            GameFiber.StartNew(
                () => Run(
                    vehicle,
                    destination,
                    heading
                ),
                "BetterBackup.FinalApproach"
            );
        }

        private static void Run(
            Vehicle vehicle,
            Vector3 destination,
            float heading
        )
        {
            try
            {
                Game.LogTrivial(
                    "[BetterBackup] Final approach armed. " +
                    $"Vehicle={vehicle.Model.Name}, " +
                    $"Target=({destination.X:F2}, " +
                    $"{destination.Y:F2}, " +
                    $"{destination.Z:F2}), " +
                    $"Heading={heading:F1}"
                );

                // =====================================================
                // PHASE 1
                //
                // PR owns the normal response until approximately 30m.
                // =====================================================

                while (vehicle.Exists())
                {
                    /*
                     * NEW:
                     *
                     * If the user cancels the response through PR while
                     * the unit is still travelling toward us, BetterBackup
                     * must immediately abandon its requested parking spot.
                     */
                    if (PRTaskBridge.IsResponseCancelled(
                        vehicle))
                    {
                        Game.LogTrivial(
                            "[BetterBackup] PR dismissed response during " +
                            "normal approach. BetterBackup final approach cancelled."
                        );

                        return;
                    }

                    float distance =
                        Distance2D(
                            vehicle.Position,
                            destination
                        );

                    if (distance <= TakeoverDistance)
                    {
                        break;
                    }

                    GameFiber.Yield();
                }

                if (!vehicle.Exists())
                {
                    return;
                }

                /*
                 * Race check before BetterBackup takes over.
                 */
                if (PRTaskBridge.IsResponseCancelled(
                    vehicle))
                {
                    Game.LogTrivial(
                        "[BetterBackup] PR dismissed response before " +
                        "final-approach takeover."
                    );

                    return;
                }

                Ped driver =
                    vehicle.Driver;

                if (driver == null ||
                    !driver.Exists())
                {
                    Game.LogTrivial(
                        "[BetterBackup] Final approach aborted: no driver."
                    );

                    return;
                }

                Game.LogTrivial(
                    "[BetterBackup] Taking over final approach. " +
                    $"Distance={Distance2D(vehicle.Position, destination):F2}m"
                );

                /*
                 * DO NOT stop PR's RespondToPosition fiber here.
                 *
                 * We need PR's state machine alive so it can either:
                 *
                 * 1. naturally complete RespondToPosition,
                 * 2. respond to PRTaskBridge when force teleport is pressed,
                 * 3. or transition to Dismiss if the user cancels.
                 */
                driver.Tasks.Clear();

                driver.KeepTasks =
                    true;

                IssueDriveTask(
                    driver,
                    vehicle,
                    destination
                );

                string teleportKeyText =
                    BetterBackupConfig.GetDisplayName(
                        BetterBackupConfig.ForceTeleportKey
                    );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    $"Press ~y~{teleportKeyText}~s~ to teleport the unit " +
                    "to your requested location."
                );

                bool teleportKeyWasDown =
                    Game.IsKeyDown(
                        BetterBackupConfig.ForceTeleportKey
                    );

                DateTime lastDriveTaskRefresh =
                    DateTime.UtcNow;

                // =====================================================
                // PHASE 2
                // =====================================================

                while (vehicle.Exists())
                {
                    // =================================================
                    // PR DISMISSAL
                    //
                    // THIS MUST BE CHECKED BEFORE ARRIVAL.
                    // =================================================

                    if (PRTaskBridge.IsResponseCancelled(
                        vehicle))
                    {
                        Game.LogTrivial(
                            "[BetterBackup] PR dismissed response during " +
                            "final approach. Releasing vehicle to PR."
                        );

                        if (driver != null &&
                            driver.Exists())
                        {
                            /*
                             * Stop BetterBackup from trying to preserve
                             * its DriveToPosition task.
                             *
                             * Do NOT clear PR's new Dismiss task.
                             */
                            driver.KeepTasks =
                                false;
                        }

                        return;
                    }

                    // =================================================
                    // GENUINE PR ARRIVAL
                    // =================================================

                    if (PRTaskBridge.HasAlreadyArrived(
                        vehicle))
                    {
                        Game.LogTrivial(
                            "[BetterBackup] PR accepted natural arrival. " +
                            "Applying exact requested placement."
                        );

                        FinalPlaceVehicle(
                            vehicle,
                            destination,
                            heading
                        );

                        if (driver != null &&
                            driver.Exists())
                        {
                            driver.KeepTasks =
                                false;
                        }

                        Game.LogTrivial(
                            "[BetterBackup] Natural PR arrival snapped " +
                            "to exact requested placement."
                        );

                        return;
                    }

                    Ped currentDriver =
                        vehicle.Driver;

                    /*
                     * PR may have changed task between frames.
                     */
                    if (currentDriver == null ||
                        !currentDriver.Exists())
                    {
                        /*
                         * Check dismissal first.
                         */
                        if (PRTaskBridge.IsResponseCancelled(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR dismissal detected as " +
                                "driver began leaving/changing state."
                            );

                            if (driver != null &&
                                driver.Exists())
                            {
                                driver.KeepTasks =
                                    false;
                            }

                            return;
                        }

                        if (PRTaskBridge.HasAlreadyArrived(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR arrival detected as " +
                                "driver began leaving vehicle."
                            );

                            Game.LogTrivial(
                                "[BetterBackup] Exact placement skipped because " +
                                "driver has already started exiting."
                            );

                            return;
                        }

                        Game.LogTrivial(
                            "[BetterBackup] Final approach ended: " +
                            "driver left unexpectedly."
                        );

                        return;
                    }

                    if (!driver.Exists() ||
                        currentDriver.Handle !=
                        driver.Handle)
                    {
                        if (PRTaskBridge.IsResponseCancelled(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR dismissal detected as " +
                                "vehicle driver changed."
                            );

                            return;
                        }

                        if (PRTaskBridge.HasAlreadyArrived(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR arrival detected as " +
                                "vehicle driver changed."
                            );

                            return;
                        }

                        Game.LogTrivial(
                            "[BetterBackup] Final approach ended: " +
                            "vehicle driver changed."
                        );

                        return;
                    }

                    float distance =
                        Distance2D(
                            vehicle.Position,
                            destination
                        );

                    // =================================================
                    // BETTERBACKUP NATURAL ARRIVAL
                    // =================================================

                    if (distance <= ArrivalDistance)
                    {
                        /*
                         * Cancellation always wins over arrival.
                         */
                        if (PRTaskBridge.IsResponseCancelled(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR dismissal detected at " +
                                "BetterBackup arrival threshold. Arrival cancelled."
                            );

                            driver.KeepTasks =
                                false;

                            return;
                        }

                        Game.LogTrivial(
                            "[BetterBackup] Natural final arrival reached. " +
                            $"Distance={distance:F2}m"
                        );

                        if (PRTaskBridge.HasAlreadyArrived(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR already accepted arrival. " +
                                "Applying exact requested placement."
                            );

                            FinalPlaceVehicle(
                                vehicle,
                                destination,
                                heading
                            );

                            if (driver.Exists())
                            {
                                driver.KeepTasks =
                                    false;
                            }

                            return;
                        }

                        CompleteArrival(
                            vehicle,
                            driver,
                            destination,
                            heading,
                            false
                        );

                        return;
                    }

                    // =================================================
                    // CONFIGURABLE FORCE-TELEPORT KEY
                    // =================================================

                    bool teleportKeyDown =
                        Game.IsKeyDown(
                            BetterBackupConfig.ForceTeleportKey
                        );

                    if (teleportKeyDown &&
                        !teleportKeyWasDown)
                    {
                        /*
                         * If the PR response has just been cancelled,
                         * force teleport must NOT override that cancellation.
                         */
                        if (PRTaskBridge.IsResponseCancelled(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] Force teleport ignored because " +
                                "PR is dismissing the unit."
                            );

                            driver.KeepTasks =
                                false;

                            return;
                        }

                        Game.LogTrivial(
                            "[BetterBackup] " +
                            $"{BetterBackupConfig.GetDisplayName(BetterBackupConfig.ForceTeleportKey)} " +
                            "pressed - forcing final arrival."
                        );

                        if (PRTaskBridge.HasAlreadyArrived(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR already accepted arrival " +
                                "before force completion. Applying exact placement."
                            );

                            FinalPlaceVehicle(
                                vehicle,
                                destination,
                                heading
                            );

                            if (driver.Exists())
                            {
                                driver.KeepTasks =
                                    false;
                            }

                            return;
                        }

                        CompleteArrival(
                            vehicle,
                            driver,
                            destination,
                            heading,
                            true
                        );

                        return;
                    }

                    teleportKeyWasDown =
                        teleportKeyDown;

                    // =================================================
                    // KEEP BETTERBACKUP FINAL DRIVE DOMINANT
                    // =================================================

                    if ((DateTime.UtcNow -
                         lastDriveTaskRefresh)
                        .TotalMilliseconds >=
                        DriveTaskRefreshMilliseconds)
                    {
                        /*
                         * Don't issue another BetterBackup driving task
                         * if PR has begun dismissing the unit.
                         */
                        if (PRTaskBridge.IsResponseCancelled(
                            vehicle))
                        {
                            Game.LogTrivial(
                                "[BetterBackup] PR dismissed unit before " +
                                "final-drive refresh."
                            );

                            driver.KeepTasks =
                                false;

                            return;
                        }

                        IssueDriveTask(
                            driver,
                            vehicle,
                            destination
                        );

                        lastDriveTaskRefresh =
                            DateTime.UtcNow;
                    }

                    GameFiber.Yield();
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Final approach ERROR: " +
                    ex
                );
            }
        }

        // =============================================================
        // COMPLETE ARRIVAL WHILE PR IS STILL RESPONDTOPOSITION
        // =============================================================

        private static void CompleteArrival(
            Vehicle vehicle,
            Ped driver,
            Vector3 destination,
            float heading,
            bool manualTeleport
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return;
            }

            /*
             * Dismissal takes absolute priority.
             */
            if (PRTaskBridge.IsResponseCancelled(
                vehicle))
            {
                Game.LogTrivial(
                    "[BetterBackup] CompleteArrival aborted: " +
                    "PR is dismissing the unit."
                );

                if (driver != null &&
                    driver.Exists())
                {
                    driver.KeepTasks =
                        false;
                }

                return;
            }

            /*
             * Race check.
             */
            if (PRTaskBridge.HasAlreadyArrived(
                vehicle))
            {
                /*
                 * Another cancellation race check before physically
                 * moving the vehicle.
                 */
                if (PRTaskBridge.IsResponseCancelled(
                    vehicle))
                {
                    if (driver != null &&
                        driver.Exists())
                    {
                        driver.KeepTasks =
                            false;
                    }

                    return;
                }

                Game.LogTrivial(
                    "[BetterBackup] PR completed arrival before bridge call. " +
                    "Applying exact placement."
                );

                FinalPlaceVehicle(
                    vehicle,
                    destination,
                    heading
                );

                if (driver != null &&
                    driver.Exists())
                {
                    driver.KeepTasks =
                        false;
                }

                return;
            }

            bool prCompleted =
                PRTaskBridge.TryCompleteRespondToPosition(
                    vehicle
                );

            if (!prCompleted)
            {
                /*
                 * A PR dismissal may be why completion failed.
                 */
                if (PRTaskBridge.IsResponseCancelled(
                    vehicle))
                {
                    Game.LogTrivial(
                        "[BetterBackup] PR arrival completion abandoned " +
                        "because unit is being dismissed."
                    );

                    if (driver != null &&
                        driver.Exists())
                    {
                        driver.KeepTasks =
                            false;
                    }

                    return;
                }

                if (PRTaskBridge.HasAlreadyArrived(
                    vehicle))
                {
                    Game.LogTrivial(
                        "[BetterBackup] PR completed arrival during " +
                        "bridge transition. Applying exact placement."
                    );

                    FinalPlaceVehicle(
                        vehicle,
                        destination,
                        heading
                    );

                    if (driver != null &&
                        driver.Exists())
                    {
                        driver.KeepTasks =
                            false;
                    }

                    return;
                }

                Game.LogTrivial(
                    "[BetterBackup] PR arrival completion genuinely failed."
                );

                if (driver != null &&
                    driver.Exists())
                {
                    driver.KeepTasks =
                        false;
                }

                Game.DisplayNotification(
                    "~r~BetterBackup~s~<br>" +
                    "Could not complete PR arrival."
                );

                return;
            }

            /*
             * Give PR a frame to process Success.
             */
            GameFiber.Yield();

            if (!vehicle.Exists())
            {
                return;
            }

            /*
             * Extremely narrow race:
             *
             * If PR changed to Dismiss during that yielded frame,
             * don't snap the vehicle.
             */
            if (PRTaskBridge.IsResponseCancelled(
                vehicle))
            {
                Game.LogTrivial(
                    "[BetterBackup] PR dismissal detected after arrival " +
                    "bridge completion. Final placement cancelled."
                );

                if (driver != null &&
                    driver.Exists())
                {
                    driver.KeepTasks =
                        false;
                }

                return;
            }

            FinalPlaceVehicle(
                vehicle,
                destination,
                heading
            );

            if (driver != null &&
                driver.Exists())
            {
                driver.KeepTasks =
                    false;
            }

            Game.LogTrivial(
                manualTeleport
                    ? "[BetterBackup] Forced PR arrival placement complete."
                    : "[BetterBackup] Natural PR arrival placement complete."
            );
        }

        // =============================================================
        // EXACT PLACEMENT
        // =============================================================

        private static void FinalPlaceVehicle(
            Vehicle vehicle,
            Vector3 destination,
            float heading
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return;
            }

            vehicle.IsPositionFrozen =
                true;

            StopVehicleMotion(
                vehicle
            );

            vehicle.Position =
                new Vector3(
                    destination.X,
                    destination.Y,
                    destination.Z + 2.0f
                );

            vehicle.Heading =
                NormalizeHeading(
                    heading
                );

            GameFiber.Yield();

            if (!vehicle.Exists())
            {
                return;
            }

            try
            {
                NativeFunction.Natives
                    .SET_VEHICLE_ON_GROUND_PROPERLY<bool>(
                        vehicle
                    );
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] SET_VEHICLE_ON_GROUND_PROPERLY failed: " +
                    ex.Message
                );
            }

            GameFiber.Yield();

            if (!vehicle.Exists())
            {
                return;
            }

            vehicle.Heading =
                NormalizeHeading(
                    heading
                );

            StopVehicleMotion(
                vehicle
            );

            vehicle.IsPositionFrozen =
                false;

            Game.LogTrivial(
                "[BetterBackup] Vehicle final-grounded. " +
                $"Position=({vehicle.Position.X:F2}, " +
                $"{vehicle.Position.Y:F2}, " +
                $"{vehicle.Position.Z:F2}), " +
                $"Heading={vehicle.Heading:F1}"
            );
        }

        // =============================================================
        // FINAL DRIVING
        // =============================================================

        private static void IssueDriveTask(
            Ped driver,
            Vehicle vehicle,
            Vector3 destination
        )
        {
            if (driver == null ||
                !driver.Exists() ||
                vehicle == null ||
                !vehicle.Exists())
            {
                return;
            }

            Ped currentDriver =
                vehicle.Driver;

            if (currentDriver == null ||
                !currentDriver.Exists())
            {
                return;
            }

            if (currentDriver.Handle !=
                driver.Handle)
            {
                return;
            }

            driver.Tasks.DriveToPosition(
                vehicle,
                destination,
                FinalApproachSpeed,
                VehicleDrivingFlags.Normal |
                VehicleDrivingFlags.StopAtDestination,
                FinalDriveAcceptedDistance
            );
        }

        private static void StopVehicleMotion(
            Vehicle vehicle
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return;
            }

            vehicle.Velocity =
                Vector3.Zero;

            vehicle.AngularVelocity =
                new Rotator(
                    0f,
                    0f,
                    0f
                );
        }

        private static float Distance2D(
            Vector3 a,
            Vector3 b
        )
        {
            float dx =
                a.X - b.X;

            float dy =
                a.Y - b.Y;

            return (float)Math.Sqrt(
                (dx * dx) +
                (dy * dy)
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
    }
}