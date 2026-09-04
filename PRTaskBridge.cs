using System;
using System.Collections;
using System.Reflection;
using Rage;
using PolicingRedefined.API;

namespace BackupPlacement
{
    public static class PRTaskBridge
    {
        private static readonly Assembly PrAssembly =
            typeof(BackupAPI).Assembly;

        private static readonly Type BackupControllerType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.BackupController",
                false
            );

        private static readonly Type BackupUnitType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.Entities.BackupUnit",
                false
            );

        private static readonly Type BackupTaskStatusType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.Entities.BackupTasks.EBackupTaskStatus",
                false
            );

        private static readonly Type BackupTaskTypeEnum =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.Entities.BackupTasks.EBackupTask",
                false
            );

        private static readonly Type RespondToPositionType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.Entities.BackupTasks.BackupUnitTasks.RespondToPosition",
                false
            );

        // ============================================================
        // RETURN CURRENT PR TASK NAME
        // ============================================================

        public static string GetCurrentTaskName(
            Vehicle rageVehicle
        )
        {
            try
            {
                if (!TryResolveContext(
                    rageVehicle,
                    out object backupUnit,
                    out object taskManager,
                    out object currentTask))
                {
                    return null;
                }

                return currentTask?.GetType().Name;
            }
            catch (Exception ex)
            {
                Log(
                    "GetCurrentTaskName ERROR: " +
                    ex.Message
                );

                return null;
            }
        }

        // ============================================================
        // HAS PR CANCELLED / DISMISSED THIS RESPONSE?
        //
        // IMPORTANT:
        //
        // Dismiss is NOT an arrival.
        //
        // If PR changes the unit to Dismiss while BetterBackup is
        // controlling its parking response, BetterBackup must stop
        // immediately and leave the unit to PR.
        // ============================================================

        public static bool IsResponseCancelled(
            Vehicle rageVehicle
        )
        {
            try
            {
                string taskName =
                    GetCurrentTaskName(
                        rageVehicle
                    );

                if (string.IsNullOrEmpty(
                    taskName))
                {
                    return false;
                }

                return string.Equals(
                    taskName,
                    "Dismiss",
                    StringComparison.OrdinalIgnoreCase
                );
            }
            catch (Exception ex)
            {
                Log(
                    "IsResponseCancelled ERROR: " +
                    ex.Message
                );

                return false;
            }
        }

        // ============================================================
        // HAS PR ALREADY ACCEPTED A GENUINE ARRIVAL?
        //
        // LeaveVehicle:
        // PR accepted RespondToPosition and crew is getting out.
        //
        // CruiseWithVehicle:
        // PR has progressed beyond RespondToPosition normally.
        //
        // Dismiss IS DELIBERATELY NOT INCLUDED.
        // ============================================================

        public static bool HasAlreadyArrived(
            Vehicle rageVehicle
        )
        {
            try
            {
                string taskName =
                    GetCurrentTaskName(
                        rageVehicle
                    );

                if (string.IsNullOrEmpty(
                    taskName))
                {
                    return false;
                }

                if (string.Equals(
                    taskName,
                    "LeaveVehicle",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(
                    taskName,
                    "CruiseWithVehicle",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log(
                    "HasAlreadyArrived ERROR: " +
                    ex.Message
                );

                return false;
            }
        }

        // ============================================================
        // FORCE RESPONDTOPOSITION -> SUCCESS
        //
        // This remains the known-working force-arrival path.
        // ============================================================

        public static bool TryCompleteRespondToPosition(
            Vehicle rageVehicle
        )
        {
            try
            {
                /*
                 * Never turn a PR dismissal into a successful arrival.
                 */
                if (IsResponseCancelled(
                    rageVehicle))
                {
                    Log(
                        "Complete: Unit is being dismissed. " +
                        "Arrival completion aborted."
                    );

                    return false;
                }

                if (!TryResolveContext(
                    rageVehicle,
                    out object backupUnit,
                    out object taskManager,
                    out object currentTask))
                {
                    return false;
                }

                Type currentTaskType =
                    currentTask.GetType();

                Log(
                    "Complete: Matching PR BackupUnit found: " +
                    backupUnit
                );

                Log(
                    "Complete: Current PR task: " +
                    currentTaskType.FullName
                );

                if (RespondToPositionType == null ||
                    !RespondToPositionType.IsAssignableFrom(
                        currentTaskType))
                {
                    Log(
                        "Complete: Current task is not RespondToPosition."
                    );

                    return false;
                }

                if (BackupTaskStatusType == null ||
                    BackupTaskTypeEnum == null)
                {
                    Log(
                        "Complete: Required PR task enums were not found."
                    );

                    return false;
                }

                object successValue =
                    Enum.Parse(
                        BackupTaskStatusType,
                        "Success"
                    );

                object respondToPositionValue =
                    Enum.Parse(
                        BackupTaskTypeEnum,
                        "RespondToPosition"
                    );

                FieldInfo statusField =
                    FindField(
                        currentTaskType,
                        "Status"
                    );

                if (statusField == null)
                {
                    Log(
                        "Complete: BackupTask.Status field not found."
                    );

                    return false;
                }

                object oldStatus =
                    statusField.GetValue(
                        currentTask
                    );

                Log(
                    "Complete: RespondToPosition status before completion = " +
                    oldStatus
                );

                // =====================================================
                // STOP LIVE RESPONDTOPOSITION TASK
                // =====================================================

                MethodInfo stopMethod =
                    FindMethod(
                        currentTaskType,
                        "Stop",
                        Type.EmptyTypes
                    );

                if (stopMethod == null)
                {
                    Log(
                        "Complete: EntityTask.Stop() could not be found."
                    );

                    return false;
                }

                /*
                 * One more race check immediately before mutation.
                 */
                if (IsResponseCancelled(
                    rageVehicle))
                {
                    Log(
                        "Complete: Dismiss detected before task mutation. " +
                        "Arrival completion aborted."
                    );

                    return false;
                }

                Log(
                    "Complete: Ensuring RespondToPosition fiber is stopped..."
                );

                stopMethod.Invoke(
                    currentTask,
                    null
                );

                // =====================================================
                // MARK ACTUAL TASK SUCCESS
                // =====================================================

                statusField.SetValue(
                    currentTask,
                    successValue
                );

                Type taskManagerType =
                    taskManager.GetType();

                // =====================================================
                // LAST STATUS
                // =====================================================

                FieldInfo lastStatusField =
                    FindField(
                        taskManagerType,
                        "_lastTaskStatus"
                    );

                if (lastStatusField != null)
                {
                    lastStatusField.SetValue(
                        taskManager,
                        successValue
                    );

                    Log(
                        "Complete: _lastTaskStatus = Success."
                    );
                }
                else
                {
                    PropertyInfo lastStatusProperty =
                        FindProperty(
                            taskManagerType,
                            "LastTaskStatus"
                        );

                    if (lastStatusProperty != null)
                    {
                        MethodInfo setter =
                            lastStatusProperty.GetSetMethod(
                                true
                            );

                        if (setter != null)
                        {
                            setter.Invoke(
                                taskManager,
                                new[]
                                {
                                    successValue
                                }
                            );

                            Log(
                                "Complete: LastTaskStatus = Success."
                            );
                        }
                    }
                }

                // =====================================================
                // LAST TASK
                // =====================================================

                FieldInfo lastTaskField =
                    FindField(
                        taskManagerType,
                        "<LastTask>k__BackingField"
                    );

                if (lastTaskField != null)
                {
                    lastTaskField.SetValue(
                        taskManager,
                        respondToPositionValue
                    );

                    Log(
                        "Complete: LastTask = RespondToPosition."
                    );
                }
                else
                {
                    PropertyInfo lastTaskProperty =
                        FindProperty(
                            taskManagerType,
                            "LastTask"
                        );

                    if (lastTaskProperty != null)
                    {
                        MethodInfo setter =
                            lastTaskProperty.GetSetMethod(
                                true
                            );

                        if (setter != null)
                        {
                            setter.Invoke(
                                taskManager,
                                new[]
                                {
                                    respondToPositionValue
                                }
                            );

                            Log(
                                "Complete: LastTask = RespondToPosition."
                            );
                        }
                    }
                }

                // =====================================================
                // OPTIONAL PR EVENT
                // =====================================================

                FieldInfo updateDelegateField =
                    FindField(
                        taskManagerType,
                        "OnUnitTaskUpdate"
                    );

                if (updateDelegateField != null)
                {
                    Delegate updateDelegate =
                        updateDelegateField.GetValue(
                            taskManager
                        ) as Delegate;

                    if (updateDelegate != null)
                    {
                        Log(
                            "Complete: Invoking OnUnitTaskUpdate."
                        );

                        updateDelegate.DynamicInvoke(
                            backupUnit,
                            respondToPositionValue,
                            successValue
                        );
                    }
                    else
                    {
                        Log(
                            "Complete: OnUnitTaskUpdate has no subscribers."
                        );
                    }
                }

                Log(
                    "Complete: Final task Status = " +
                    statusField.GetValue(
                        currentTask
                    )
                );

                if (lastStatusField != null)
                {
                    Log(
                        "Complete: Final manager LastTaskStatus = " +
                        lastStatusField.GetValue(
                            taskManager
                        )
                    );
                }

                Log(
                    "Complete: RespondToPosition successfully completed."
                );

                return true;
            }
            catch (TargetInvocationException ex)
            {
                Log(
                    "Complete TARGET INVOCATION ERROR: " +
                    (ex.InnerException ?? ex)
                );

                return false;
            }
            catch (Exception ex)
            {
                Log(
                    "Complete ERROR: " +
                    ex
                );

                return false;
            }
        }

        // ============================================================
        // RESOLVE CURRENT UNIT / TASK
        // ============================================================

        private static bool TryResolveContext(
            Vehicle rageVehicle,
            out object backupUnit,
            out object taskManager,
            out object currentTask
        )
        {
            backupUnit = null;
            taskManager = null;
            currentTask = null;

            if (rageVehicle == null ||
                !rageVehicle.Exists())
            {
                return false;
            }

            if (BackupControllerType == null ||
                BackupUnitType == null)
            {
                Log(
                    "Required Policing Redefined types could not be resolved."
                );

                return false;
            }

            backupUnit =
                FindBackupUnitForVehicle(
                    rageVehicle
                );

            if (backupUnit == null)
            {
                return false;
            }

            PropertyInfo taskManagerProperty =
                FindProperty(
                    backupUnit.GetType(),
                    "TaskManager"
                );

            if (taskManagerProperty == null)
            {
                return false;
            }

            taskManager =
                taskManagerProperty.GetValue(
                    backupUnit,
                    null
                );

            if (taskManager == null)
            {
                return false;
            }

            FieldInfo currentTaskField =
                FindField(
                    taskManager.GetType(),
                    "_currentTaskObject"
                );

            if (currentTaskField == null)
            {
                return false;
            }

            currentTask =
                currentTaskField.GetValue(
                    taskManager
                );

            return currentTask != null;
        }

        // ============================================================
        // FIND EXACT PR UNIT BY VEHICLE HANDLE
        // ============================================================

        private static object FindBackupUnitForVehicle(
            Vehicle rageVehicle
        )
        {
            FieldInfo activeUnitsField =
                FindField(
                    BackupControllerType,
                    "ActiveUnits"
                );

            if (activeUnitsField == null)
            {
                return null;
            }

            IEnumerable activeUnits =
                activeUnitsField.GetValue(
                    null
                ) as IEnumerable;

            if (activeUnits == null)
            {
                return null;
            }

            foreach (object unit in activeUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                PropertyInfo vehicleProperty =
                    FindProperty(
                        unit.GetType(),
                        "Vehicle"
                    );

                if (vehicleProperty == null)
                {
                    continue;
                }

                object prVehicle =
                    vehicleProperty.GetValue(
                        unit,
                        null
                    );

                if (prVehicle == null)
                {
                    continue;
                }

                Vehicle directVehicle =
                    prVehicle as Vehicle;

                if (directVehicle != null &&
                    directVehicle.Exists() &&
                    directVehicle.Handle ==
                    rageVehicle.Handle)
                {
                    return unit;
                }

                PropertyInfo entityProperty =
                    FindProperty(
                        prVehicle.GetType(),
                        "Entity"
                    );

                if (entityProperty == null)
                {
                    continue;
                }

                Vehicle unitVehicle =
                    entityProperty.GetValue(
                        prVehicle,
                        null
                    ) as Vehicle;

                if (unitVehicle == null ||
                    !unitVehicle.Exists())
                {
                    continue;
                }

                if (unitVehicle.Handle ==
                    rageVehicle.Handle)
                {
                    return unit;
                }
            }

            return null;
        }

        // ============================================================
        // REFLECTION HELPERS
        // ============================================================

        private static FieldInfo FindField(
            Type type,
            string name
        )
        {
            Type current =
                type;

            while (current != null)
            {
                FieldInfo field =
                    current.GetField(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                if (field != null)
                {
                    return field;
                }

                current =
                    current.BaseType;
            }

            return null;
        }

        private static PropertyInfo FindProperty(
            Type type,
            string name
        )
        {
            Type current =
                type;

            while (current != null)
            {
                PropertyInfo property =
                    current.GetProperty(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                if (property != null)
                {
                    return property;
                }

                current =
                    current.BaseType;
            }

            return null;
        }

        private static MethodInfo FindMethod(
            Type type,
            string name,
            Type[] parameterTypes
        )
        {
            Type current =
                type;

            while (current != null)
            {
                MethodInfo method =
                    current.GetMethod(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly,
                        null,
                        parameterTypes,
                        null
                    );

                if (method != null)
                {
                    return method;
                }

                current =
                    current.BaseType;
            }

            return null;
        }

        private static void Log(
            string message
        )
        {
            Game.LogTrivial(
                "[PRTaskBridge] " +
                message
            );
        }
    }
}