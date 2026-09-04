using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Rage;
using PolicingRedefined.API;

namespace BackupPlacement
{
    public class BackupWatcher
    {
        private readonly PlacementController _placementController;

        private bool _running;
        private bool _bWasDown;
        private bool _watchingForBackup;

        private DateTime _watchStarted;

        private const int WatchDurationSeconds = 20;

        /*
         * PR service-unit scanning.
         *
         * This catches units that do NOT originate from the B menu,
         * including:
         *
         * - prisoner transport
         * - tow trucks requested through Ctrl+T / PR interaction
         */
        private const int ServiceUnitScanIntervalMilliseconds = 250;

        private DateTime _lastServiceUnitScan =
            DateTime.MinValue;

        private readonly HashSet<PoolHandle> _vehiclesBeforeRequest =
            new HashSet<PoolHandle>();

        /*
         * Units detected by the independent PR service-unit scanner.
         *
         * Keeping one common set prevents the B watcher from also
         * capturing a vehicle already handed to PlacementController.
         */
        private readonly HashSet<PoolHandle> _handledServiceVehicles =
            new HashSet<PoolHandle>();

        private readonly Dictionary<uint, List<EBackupUnit>>
            _modelToUnitTypes =
                new Dictionary<uint, List<EBackupUnit>>();

        // ============================================================
        // PR REFLECTION
        // ============================================================

        private static readonly Assembly PrAssembly =
            typeof(BackupAPI).Assembly;

        private static readonly Type DispatchMenuType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.Modules.Dispatch.DispatchMenu",
                false
            );

        private static readonly Type BackupControllerType =
            PrAssembly.GetType(
                "PolicingRedefined.Backup.BackupController",
                false
            );

        /*
         * PR stores dispatched-unit information here.
         */
        private static readonly Type InputControllerType =
            PrAssembly.GetType(
                "PolicingRedefined.Engine.Core.InputController",
                false
            );

        private static readonly FieldInfo ActiveUnitsField =
            BackupControllerType != null
                ? FindField(
                    BackupControllerType,
                    "ActiveUnits"
                )
                : null;

        private static readonly FieldInfo DispatchedUnitsField =
            InputControllerType != null
                ? FindField(
                    InputControllerType,
                    "DispatchedUnits"
                )
                : null;

        public BackupWatcher(
            PlacementController placementController
        )
        {
            _placementController =
                placementController;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running =
                true;

            _lastServiceUnitScan =
                DateTime.MinValue;

            /*
             * Anything already active when BetterBackup starts must
             * NOT suddenly be treated as a newly requested service.
             */
            SnapshotExistingServiceUnits();

            GameFiber.StartNew(
                MainLoop,
                "BetterBackup.BackupWatcher"
            );

            Game.LogTrivial(
                "[BetterBackup] BackupWatcher started."
            );

            Game.LogTrivial(
                "[BetterBackup] PR reflection: " +
                $"BackupController=" +
                $"{(BackupControllerType != null ? "OK" : "MISSING")}, " +
                $"ActiveUnits=" +
                $"{(ActiveUnitsField != null ? "OK" : "MISSING")}, " +
                $"InputController=" +
                $"{(InputControllerType != null ? "OK" : "MISSING")}, " +
                $"DispatchedUnits=" +
                $"{(DispatchedUnitsField != null ? "OK" : "MISSING")}."
            );
        }

        public void Stop()
        {
            _running =
                false;

            _watchingForBackup =
                false;

            _handledServiceVehicles.Clear();

            _lastServiceUnitScan =
                DateTime.MinValue;
        }

        private void MainLoop()
        {
            while (_running)
            {
                GameFiber.Yield();

                try
                {
                    HandleBackupKey();

                    if (_watchingForBackup)
                    {
                        CheckForNewBackupVehicle();
                    }

                    if (ShouldRunServiceUnitScan())
                    {
                        CheckForNewServiceUnits();
                    }
                }
                catch (Exception ex)
                {
                    Game.LogTrivial(
                        "[BetterBackup] BackupWatcher ERROR: " +
                        ex
                    );
                }
            }
        }

        private bool ShouldRunServiceUnitScan()
        {
            DateTime now =
                DateTime.UtcNow;

            if ((now - _lastServiceUnitScan)
                .TotalMilliseconds <
                ServiceUnitScanIntervalMilliseconds)
            {
                return false;
            }

            _lastServiceUnitScan =
                now;

            return true;
        }

        // ============================================================
        // NORMAL B-MENU BACKUP WATCHER
        // ============================================================

        private void HandleBackupKey()
        {
            bool bDown =
                Game.IsKeyDown(
                    Keys.B
                );

            if (bDown &&
                !_bWasDown)
            {
                ArmWatcher();
            }

            _bWasDown =
                bDown;
        }

        private void ArmWatcher()
        {
            Game.LogTrivial(
                "[BetterBackup] B pressed - " +
                "arming PR backup watcher."
            );

            _watchingForBackup =
                false;

            SnapshotCurrentVehicles();
            BuildPRModelMap();

            _watchStarted =
                DateTime.UtcNow;

            _watchingForBackup =
                true;

            Game.LogTrivial(
                "[BetterBackup] Watching for newly " +
                "spawned PR backup vehicle."
            );
        }

        private void SnapshotCurrentVehicles()
        {
            _vehiclesBeforeRequest.Clear();

            Vehicle[] vehicles =
                World.GetAllVehicles();

            foreach (Vehicle vehicle in vehicles)
            {
                if (vehicle == null ||
                    !vehicle.Exists())
                {
                    continue;
                }

                _vehiclesBeforeRequest.Add(
                    vehicle.Handle
                );
            }

            Game.LogTrivial(
                "[BetterBackup] Snapshot contains " +
                $"{_vehiclesBeforeRequest.Count} existing vehicles."
            );
        }

        private void BuildPRModelMap()
        {
            _modelToUnitTypes.Clear();

            Ped player =
                Game.LocalPlayer.Character;

            if (player == null ||
                !player.Exists())
            {
                return;
            }

            Vector3 playerPosition =
                player.Position;

            Array unitValues =
                Enum.GetValues(
                    typeof(EBackupUnit)
                );

            foreach (object value in unitValues)
            {
                EBackupUnit unit =
                    (EBackupUnit)value;

                try
                {
                    Model[] models =
                        BackupAPI.GetLocalBackupVehicleModels(
                            unit,
                            playerPosition
                        );

                    if (models == null)
                    {
                        continue;
                    }

                    foreach (Model model in models)
                    {
                        uint hash =
                            unchecked(
                                (uint)model.Hash
                            );

                        List<EBackupUnit> types;

                        if (!_modelToUnitTypes.TryGetValue(
                            hash,
                            out types))
                        {
                            types =
                                new List<EBackupUnit>();

                            _modelToUnitTypes.Add(
                                hash,
                                types
                            );
                        }

                        if (!types.Contains(unit))
                        {
                            types.Add(
                                unit
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    Game.LogTrivial(
                        "[BetterBackup] Could not get " +
                        $"models for {unit}: " +
                        ex.Message
                    );
                }
            }

            Game.LogTrivial(
                "[BetterBackup] PR model map contains " +
                $"{_modelToUnitTypes.Count} unique vehicle models."
            );
        }

        private void CheckForNewBackupVehicle()
        {
            TimeSpan elapsed =
                DateTime.UtcNow -
                _watchStarted;

            if (elapsed.TotalSeconds >
                WatchDurationSeconds)
            {
                Game.LogTrivial(
                    "[BetterBackup] Backup watch timed out."
                );

                _watchingForBackup =
                    false;

                return;
            }

            Vehicle[] vehicles =
                World.GetAllVehicles();

            foreach (Vehicle vehicle in vehicles)
            {
                if (vehicle == null ||
                    !vehicle.Exists())
                {
                    continue;
                }

                if (_vehiclesBeforeRequest.Contains(
                    vehicle.Handle))
                {
                    continue;
                }

                /*
                 * Service scanner already owns this one.
                 */
                if (_handledServiceVehicles.Contains(
                    vehicle.Handle))
                {
                    continue;
                }

                uint modelHash =
                    unchecked(
                        (uint)vehicle.Model.Hash
                    );

                List<EBackupUnit> matchingTypes;

                if (!_modelToUnitTypes.TryGetValue(
                    modelHash,
                    out matchingTypes))
                {
                    continue;
                }

                Ped driver =
                    vehicle.Driver;

                if (driver == null ||
                    !driver.Exists())
                {
                    continue;
                }

                // =====================================================
                // PURSUIT FILTER
                // =====================================================

                if (IsCurrentPRRequestPursuit())
                {
                    Game.LogTrivial(
                        "[BetterBackup] Pursuit backup detected. " +
                        $"Ignoring vehicle {vehicle.Model.Name} / " +
                        $"{vehicle.Handle}."
                    );

                    _watchingForBackup =
                        false;

                    return;
                }

                // =====================================================
                // PR DISPATCH CLASSIFICATION
                //
                // We deliberately wait for PR to classify the candidate.
                //
                // TrafficStop belongs entirely to PR.
                // =====================================================

                string dispatchType;

                if (!TryGetPRDispatchTypeForVehicle(
                    vehicle,
                    out dispatchType))
                {
                    continue;
                }

                if (string.Equals(
                    dispatchType,
                    "TrafficStop",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Game.LogTrivial(
                        "[BetterBackup] Traffic stop backup detected. " +
                        $"Vehicle={vehicle.Model.Name} / {vehicle.Handle}. " +
                        "BetterBackup will not intercept it."
                    );

                    _watchingForBackup =
                        false;

                    return;
                }

                Game.LogTrivial(
                    "[BetterBackup] PR candidate dispatch type = " +
                    dispatchType +
                    ". BetterBackup interception allowed."
                );

                HandleCandidate(
                    vehicle,
                    matchingTypes
                );

                return;
            }
        }

        // ============================================================
        // NON-B-MENU SERVICE UNIT SCANNER
        //
        // This is the important addition.
        //
        // PR creates prisoner transport and tow units as real active
        // BackupUnit objects even when they weren't requested through
        // the B menu.
        //
        // We therefore watch ActiveUnits itself rather than watching
        // Ctrl+T.
        // ============================================================

        private void SnapshotExistingServiceUnits()
        {
            try
            {
                foreach (object unit in GetActivePRUnits())
                {
                    if (unit == null)
                    {
                        continue;
                    }

                    if (!IsPrisonerTransportUnit(unit) &&
                        !IsTowUnit(unit))
                    {
                        continue;
                    }

                    Vehicle vehicle =
                        GetVehicleFromPRUnit(
                            unit
                        );

                    if (vehicle == null ||
                        !vehicle.Exists())
                    {
                        continue;
                    }

                    _handledServiceVehicles.Add(
                        vehicle.Handle
                    );
                }

                Game.LogTrivial(
                    "[BetterBackup] Existing PR service units snapshotted: " +
                    $"{_handledServiceVehicles.Count}."
                );
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Service-unit snapshot ERROR: " +
                    ex.Message
                );
            }
        }

        private void CheckForNewServiceUnits()
        {
            try
            {
                foreach (object unit in GetActivePRUnits())
                {
                    if (unit == null)
                    {
                        continue;
                    }

                    bool prisonerTransport =
                        IsPrisonerTransportUnit(
                            unit
                        );

                    EBackupUnit towType;
                    bool towTruck =
                        TryGetTowUnitType(
                            unit,
                            out towType
                        );

                    if (!prisonerTransport &&
                        !towTruck)
                    {
                        continue;
                    }

                    Vehicle vehicle =
                        GetVehicleFromPRUnit(
                            unit
                        );

                    if (vehicle == null ||
                        !vehicle.Exists())
                    {
                        continue;
                    }

                    if (_handledServiceVehicles.Contains(
                        vehicle.Handle))
                    {
                        continue;
                    }

                    /*
                     * Wait until PR has actually put a driver into it.
                     * This avoids capturing the vehicle halfway through
                     * PR's spawn/setup sequence.
                     */
                    Ped driver =
                        vehicle.Driver;

                    if (driver == null ||
                        !driver.Exists())
                    {
                        continue;
                    }

                    /*
                     * Claim it before doing anything else so another
                     * scanner pass cannot hand it over twice.
                     */
                    _handledServiceVehicles.Add(
                        vehicle.Handle
                    );

                    /*
                     * If B happens to be armed at the same time,
                     * shield this exact service vehicle from that path.
                     */
                    _vehiclesBeforeRequest.Add(
                        vehicle.Handle
                    );

                    if (prisonerTransport)
                    {
                        Game.LogTrivial(
                            "[BetterBackup] PR prisoner transport detected " +
                            "outside normal B-menu capture. " +
                            $"Handle={vehicle.Handle}, " +
                            $"Model={vehicle.Model.Name}, " +
                            $"PRType={unit.GetType().FullName}"
                        );

                        Game.DisplayNotification(
                            "~b~BetterBackup~s~<br>" +
                            "PR backup detected:<br>" +
                            "~y~PoliceTransport~s~"
                        );

                        _placementController.BeginForBackup(
                            vehicle,
                            EBackupUnit.PoliceTransport
                        );

                        return;
                    }

                    /*
                     * Tow truck.
                     *
                     * We are only changing where the truck responds.
                     * The PR BackupUnit itself remains alive, so PR
                     * retains the tow target and its towing workflow.
                     */
                    Game.LogTrivial(
                        "[BetterBackup] PR tow truck detected " +
                        "outside normal B-menu capture. " +
                        $"Handle={vehicle.Handle}, " +
                        $"Model={vehicle.Model.Name}, " +
                        $"PRType={unit.GetType().FullName}, " +
                        $"UnitType={towType}"
                    );

                    Game.DisplayNotification(
                        "~b~BetterBackup~s~<br>" +
                        "PR service detected:<br>" +
                        $"~y~{towType}~s~"
                    );

                    _placementController.BeginForBackup(
                        vehicle,
                        towType
                    );

                    return;
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Service-unit detector ERROR: " +
                    ex.Message
                );
            }
        }

        // ============================================================
        // SERVICE UNIT IDENTIFICATION
        // ============================================================

        private static bool IsPrisonerTransportUnit(
            object unit
        )
        {
            if (unit == null)
            {
                return false;
            }

            Type type =
                unit.GetType();

            string name =
                type.Name ?? string.Empty;

            string fullName =
                type.FullName ?? string.Empty;

            if (string.Equals(
                name,
                "PrisonerTransportUnit",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullName.EndsWith(
                ".PrisonerTransportUnit",
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static bool IsTowUnit(
            object unit
        )
        {
            EBackupUnit ignored;

            return TryGetTowUnitType(
                unit,
                out ignored
            );
        }

        private static bool TryGetTowUnitType(
            object unit,
            out EBackupUnit unitType
        )
        {
            unitType =
                EBackupUnit.LargeTowTruck;

            if (unit == null)
            {
                return false;
            }

            Type type =
                unit.GetType();

            string typeName =
                type.Name ?? string.Empty;

            string fullName =
                type.FullName ?? string.Empty;

            /*
             * First use the PR unit's concrete class name.
             *
             * This is preferable to looking only at the GTA vehicle
             * model because PR supports configurable tow vehicles,
             * including flatbeds.
             */
            if (ContainsIgnoreCase(
                typeName,
                "TowTruck") ||
                ContainsIgnoreCase(
                    fullName,
                    "TowTruck"))
            {
                if (ContainsIgnoreCase(
                    typeName,
                    "Small") ||
                    ContainsIgnoreCase(
                        fullName,
                        "Small"))
                {
                    unitType =
                        EBackupUnit.SmallTowTruck;
                }
                else
                {
                    unitType =
                        EBackupUnit.LargeTowTruck;
                }

                return true;
            }

            /*
             * Some PR versions may use a more generic TowUnit class.
             */
            if (string.Equals(
                    typeName,
                    "TowUnit",
                    StringComparison.OrdinalIgnoreCase) ||
                fullName.EndsWith(
                    ".TowUnit",
                    StringComparison.OrdinalIgnoreCase))
            {
                unitType =
                    EBackupUnit.LargeTowTruck;

                return true;
            }

            /*
             * Fallback:
             *
             * inspect enum-like/unit-type members on the PR BackupUnit.
             *
             * We don't hard-code one private field name because PR's
             * internal implementation can vary.
             */
            string[] memberNames =
            {
                "BackupUnitType",
                "UnitType",
                "Type",
                "_backupUnitType",
                "_unitType",
                "<BackupUnitType>k__BackingField",
                "<UnitType>k__BackingField"
            };

            foreach (string memberName in memberNames)
            {
                PropertyInfo property =
                    FindProperty(
                        type,
                        memberName
                    );

                if (property != null &&
                    property.CanRead)
                {
                    try
                    {
                        object value =
                            property.GetValue(
                                unit,
                                null
                            );

                        if (TryParseTowType(
                            value,
                            out unitType))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }

                FieldInfo field =
                    FindField(
                        type,
                        memberName
                    );

                if (field != null)
                {
                    try
                    {
                        object value =
                            field.GetValue(
                                unit
                            );

                        if (TryParseTowType(
                            value,
                            out unitType))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private static bool TryParseTowType(
            object value,
            out EBackupUnit unitType
        )
        {
            unitType =
                EBackupUnit.LargeTowTruck;

            if (value == null)
            {
                return false;
            }

            string text =
                value.ToString();

            if (string.Equals(
                text,
                "SmallTowTruck",
                StringComparison.OrdinalIgnoreCase))
            {
                unitType =
                    EBackupUnit.SmallTowTruck;

                return true;
            }

            if (string.Equals(
                text,
                "LargeTowTruck",
                StringComparison.OrdinalIgnoreCase))
            {
                unitType =
                    EBackupUnit.LargeTowTruck;

                return true;
            }

            return false;
        }

        private static bool ContainsIgnoreCase(
            string source,
            string value
        )
        {
            if (string.IsNullOrEmpty(source) ||
                string.IsNullOrEmpty(value))
            {
                return false;
            }

            return source.IndexOf(
                value,
                StringComparison.OrdinalIgnoreCase
            ) >= 0;
        }

        // ============================================================
        // PR DISPATCH TYPE RESOLUTION
        // ============================================================

        private bool TryGetPRDispatchTypeForVehicle(
            Vehicle vehicle,
            out string dispatchType
        )
        {
            dispatchType =
                null;

            if (vehicle == null ||
                !vehicle.Exists())
            {
                return false;
            }

            object unit =
                FindPRUnitForVehicle(
                    vehicle
                );

            if (unit == null)
            {
                return false;
            }

            return TryGetDispatchTypeForPRUnit(
                unit,
                out dispatchType
            );
        }

        private object FindPRUnitForVehicle(
            Vehicle vehicle
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return null;
            }

            foreach (object unit in GetActivePRUnits())
            {
                if (unit == null)
                {
                    continue;
                }

                Vehicle unitVehicle =
                    GetVehicleFromPRUnit(
                        unit
                    );

                if (unitVehicle == null ||
                    !unitVehicle.Exists())
                {
                    continue;
                }

                if (unitVehicle.Handle ==
                    vehicle.Handle)
                {
                    return unit;
                }
            }

            return null;
        }

        private bool TryGetDispatchTypeForPRUnit(
            object targetUnit,
            out string dispatchType
        )
        {
            dispatchType =
                null;

            if (targetUnit == null ||
                DispatchedUnitsField == null)
            {
                return false;
            }

            object dictionaryObject;

            try
            {
                dictionaryObject =
                    DispatchedUnitsField.GetValue(
                        null
                    );
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Could not read " +
                    "InputController.DispatchedUnits: " +
                    ex.Message
                );

                return false;
            }

            IEnumerable dictionary =
                dictionaryObject as IEnumerable;

            if (dictionary == null)
            {
                return false;
            }

            List<object> entries =
                new List<object>();

            try
            {
                foreach (object entry in dictionary)
                {
                    if (entry != null)
                    {
                        entries.Add(
                            entry
                        );
                    }
                }
            }
            catch
            {
                return false;
            }

            foreach (object entry in entries)
            {
                Type entryType =
                    entry.GetType();

                PropertyInfo keyProperty =
                    FindProperty(
                        entryType,
                        "Key"
                    );

                PropertyInfo valueProperty =
                    FindProperty(
                        entryType,
                        "Value"
                    );

                if (keyProperty == null ||
                    valueProperty == null)
                {
                    continue;
                }

                object key;

                try
                {
                    key =
                        keyProperty.GetValue(
                            entry,
                            null
                        );
                }
                catch
                {
                    continue;
                }

                if (key == null)
                {
                    continue;
                }

                bool sameUnit =
                    ReferenceEquals(
                        key,
                        targetUnit
                    );

                if (!sameUnit)
                {
                    Vehicle keyVehicle =
                        GetVehicleFromPRUnit(
                            key
                        );

                    Vehicle targetVehicle =
                        GetVehicleFromPRUnit(
                            targetUnit
                        );

                    if (keyVehicle != null &&
                        keyVehicle.Exists() &&
                        targetVehicle != null &&
                        targetVehicle.Exists() &&
                        keyVehicle.Handle ==
                            targetVehicle.Handle)
                    {
                        sameUnit =
                            true;
                    }
                }

                if (!sameUnit)
                {
                    continue;
                }

                object outerTuple;

                try
                {
                    outerTuple =
                        valueProperty.GetValue(
                            entry,
                            null
                        );
                }
                catch
                {
                    return false;
                }

                if (outerTuple == null)
                {
                    return false;
                }

                PropertyInfo item2Property =
                    FindProperty(
                        outerTuple.GetType(),
                        "Item2"
                    );

                if (item2Property == null)
                {
                    return false;
                }

                object typeValue;

                try
                {
                    typeValue =
                        item2Property.GetValue(
                            outerTuple,
                            null
                        );
                }
                catch
                {
                    return false;
                }

                if (typeValue == null)
                {
                    return false;
                }

                dispatchType =
                    typeValue.ToString();

                Game.LogTrivial(
                    "[BetterBackup] PR dispatch type resolved: " +
                    dispatchType
                );

                return true;
            }

            return false;
        }

        // ============================================================
        // ACTIVE PR UNITS
        // ============================================================

        private IEnumerable GetActivePRUnits()
        {
            if (ActiveUnitsField == null)
            {
                yield break;
            }

            IEnumerable activeUnits;

            try
            {
                activeUnits =
                    ActiveUnitsField.GetValue(
                        null
                    ) as IEnumerable;
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Could not read PR ActiveUnits: " +
                    ex.Message
                );

                yield break;
            }

            if (activeUnits == null)
            {
                yield break;
            }

            /*
             * Snapshot before yielding.
             *
             * PR can mutate ActiveUnits while we're scanning it.
             */
            List<object> snapshot =
                new List<object>();

            try
            {
                foreach (object unit in activeUnits)
                {
                    if (unit != null)
                    {
                        snapshot.Add(
                            unit
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Could not snapshot PR ActiveUnits: " +
                    ex.Message
                );

                yield break;
            }

            foreach (object unit in snapshot)
            {
                yield return unit;
            }
        }

        private static Vehicle GetVehicleFromPRUnit(
            object unit
        )
        {
            if (unit == null)
            {
                return null;
            }

            PropertyInfo vehicleProperty =
                FindProperty(
                    unit.GetType(),
                    "Vehicle"
                );

            if (vehicleProperty == null)
            {
                return null;
            }

            object prVehicle;

            try
            {
                prVehicle =
                    vehicleProperty.GetValue(
                        unit,
                        null
                    );
            }
            catch
            {
                return null;
            }

            if (prVehicle == null)
            {
                return null;
            }

            /*
             * Some PR versions expose Rage.Vehicle directly.
             */
            Vehicle directVehicle =
                prVehicle as Vehicle;

            if (directVehicle != null)
            {
                return directVehicle;
            }

            /*
             * Other PR wrappers expose the actual Rage entity through
             * an Entity property.
             */
            PropertyInfo entityProperty =
                FindProperty(
                    prVehicle.GetType(),
                    "Entity"
                );

            if (entityProperty == null)
            {
                return null;
            }

            try
            {
                return
                    entityProperty.GetValue(
                        prVehicle,
                        null
                    ) as Vehicle;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // PURSUIT DETECTION
        // ============================================================

        private bool IsCurrentPRRequestPursuit()
        {
            try
            {
                if (DispatchMenuType == null)
                {
                    return false;
                }

                object menu =
                    GetDispatchMenuInstance();

                if (menu == null)
                {
                    return false;
                }

                FieldInfo backupTypeField =
                    FindField(
                        DispatchMenuType,
                        "_backupType"
                    );

                if (backupTypeField == null)
                {
                    return false;
                }

                object scroller =
                    backupTypeField.GetValue(
                        menu
                    );

                if (scroller == null)
                {
                    return false;
                }

                string[] propertyNames =
                {
                    "SelectedItem",
                    "SelectedValue",
                    "CurrentItem",
                    "CurrentValue",
                    "Value",
                    "Selected"
                };

                foreach (string propertyName in propertyNames)
                {
                    PropertyInfo property =
                        FindProperty(
                            scroller.GetType(),
                            propertyName
                        );

                    if (property == null ||
                        !property.CanRead)
                    {
                        continue;
                    }

                    object value;

                    try
                    {
                        value =
                            property.GetValue(
                                scroller,
                                null
                            );
                    }
                    catch
                    {
                        continue;
                    }

                    if (IsPursuitValue(
                        value))
                    {
                        Game.LogTrivial(
                            "[BetterBackup] Pursuit filter matched " +
                            $"property {propertyName}=Pursuit."
                        );

                        return true;
                    }
                }

                string[] fieldNames =
                {
                    "_selectedItem",
                    "_selectedValue",
                    "_currentItem",
                    "_currentValue",
                    "_value",
                    "<SelectedItem>k__BackingField",
                    "<SelectedValue>k__BackingField",
                    "<CurrentItem>k__BackingField",
                    "<CurrentValue>k__BackingField",
                    "<Value>k__BackingField"
                };

                foreach (string fieldName in fieldNames)
                {
                    FieldInfo field =
                        FindField(
                            scroller.GetType(),
                            fieldName
                        );

                    if (field == null)
                    {
                        continue;
                    }

                    object value;

                    try
                    {
                        value =
                            field.GetValue(
                                scroller
                            );
                    }
                    catch
                    {
                        continue;
                    }

                    if (IsPursuitValue(
                        value))
                    {
                        Game.LogTrivial(
                            "[BetterBackup] Pursuit filter matched " +
                            $"field {fieldName}=Pursuit."
                        );

                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Pursuit filter ERROR: " +
                    ex.Message
                );

                return false;
            }
        }

        private object GetDispatchMenuInstance()
        {
            if (DispatchMenuType == null)
            {
                return null;
            }

            PropertyInfo instanceProperty =
                DispatchMenuType.GetProperty(
                    "Instance",
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (instanceProperty != null)
            {
                try
                {
                    object instance =
                        instanceProperty.GetValue(
                            null,
                            null
                        );

                    if (instance != null)
                    {
                        return instance;
                    }
                }
                catch
                {
                }
            }

            FieldInfo instanceField =
                DispatchMenuType.GetField(
                    "<Instance>k__BackingField",
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            if (instanceField != null)
            {
                try
                {
                    return
                        instanceField.GetValue(
                            null
                        );
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool IsPursuitValue(
            object value
        )
        {
            if (value == null)
            {
                return false;
            }

            return string.Equals(
                value.ToString(),
                "Pursuit",
                StringComparison.OrdinalIgnoreCase
            );
        }

        // ============================================================
        // NORMAL CANDIDATE
        // ============================================================

        private void HandleCandidate(
            Vehicle vehicle,
            List<EBackupUnit> matchingTypes
        )
        {
            _watchingForBackup =
                false;

            string typeText =
                string.Join(
                    ", ",
                    matchingTypes.Select(
                        type => type.ToString()
                    )
                );

            Game.LogTrivial(
                "[BetterBackup] Candidate PR backup captured. " +
                $"Handle={vehicle.Handle}, " +
                $"Model={vehicle.Model.Name}, " +
                $"PossibleUnitTypes=[{typeText}], " +
                $"Position=(" +
                $"{vehicle.Position.X:F2}, " +
                $"{vehicle.Position.Y:F2}, " +
                $"{vehicle.Position.Z:F2})"
            );

            /*
             * A tow/transport caught through the B watcher is marked
             * handled as well, preventing the independent service
             * scanner from opening a second placement UI.
             */
            if (matchingTypes.Any(
                type =>
                    type == EBackupUnit.PoliceTransport ||
                    type == EBackupUnit.SmallTowTruck ||
                    type == EBackupUnit.LargeTowTruck))
            {
                _handledServiceVehicles.Add(
                    vehicle.Handle
                );
            }

            if (matchingTypes.Count == 1)
            {
                EBackupUnit unitType =
                    matchingTypes[0];

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "PR backup detected:<br>" +
                    $"~y~{unitType}~s~"
                );

                _placementController.BeginForBackup(
                    vehicle,
                    unitType
                );
            }
            else
            {
                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "PR backup vehicle detected.<br>" +
                    "~o~Unit type ambiguous.~s~"
                );

                _placementController.BeginForBackup(
                    vehicle,
                    null
                );
            }
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
    }
}