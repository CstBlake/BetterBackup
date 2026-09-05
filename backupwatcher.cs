using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        /*
         * Snapshot of PR ActiveUnits when B is pressed.
         *
         * This is the authoritative B-menu detector.
         *
         * We use reference identity rather than Equals(), because what
         * matters is whether this exact PR BackupUnit object existed
         * before the request.
         */
        private readonly HashSet<object> _prUnitsBeforeRequest =
            new HashSet<object>(
                ReferenceEqualityComparer.Instance
            );

        /*
         * Units detected by the independent PR service-unit scanner.
         *
         * Keeping one common set prevents the B watcher from also
         * capturing a vehicle already handed to PlacementController.
         */
        private readonly HashSet<PoolHandle> _handledServiceVehicles =
            new HashSet<PoolHandle>();

        /*
         * Built-in PR model map.
         *
         * IMPORTANT:
         *
         * This is metadata/fallback only.
         *
         * A vehicle does NOT need to appear in this dictionary in
         * order for BetterBackup to intercept a B-menu request.
         *
         * SpecialUnits.xml vehicles, addon vehicles and other custom
         * models can therefore still be captured.
         */
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
             * Make sure SpecialUnits.xml metadata has been loaded.
             *
             * SpecialUnitsReader itself guards against repeated loads,
             * so this remains a one-time disk read per plugin load.
             */
            SpecialUnitsReader.Load();

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
            _prUnitsBeforeRequest.Clear();

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
                        CheckForNewBackupUnit();
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

            /*
             * Snapshot only PR's currently active backup units.
             *
             * BetterBackup does NOT scan/snapshot every GTA vehicle.
             */
            SnapshotCurrentPRUnits();

            /*
             * Build PR's built-in model map only for metadata.
             *
             * This is NOT used to decide whether a newly dispatched PR
             * unit is eligible for BetterBackup.
             */
            BuildPRModelMap();

            _watchStarted =
                DateTime.UtcNow;

            _watchingForBackup =
                true;

            Game.LogTrivial(
                "[BetterBackup] Watching for newly " +
                "created PR ActiveUnit."
            );
        }

        private void SnapshotCurrentPRUnits()
        {
            _prUnitsBeforeRequest.Clear();

            try
            {
                foreach (object unit in GetActivePRUnits())
                {
                    if (unit == null)
                    {
                        continue;
                    }

                    _prUnitsBeforeRequest.Add(
                        unit
                    );
                }

                Game.LogTrivial(
                    "[BetterBackup] PR ActiveUnits snapshot contains " +
                    $"{_prUnitsBeforeRequest.Count} existing units."
                );
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Could not snapshot PR ActiveUnits " +
                    "for B-menu request: " +
                    ex.Message
                );
            }
        }

        // ============================================================
        // BUILT-IN PR MODEL MAP
        //
        // This is metadata only.
        //
        // It is useful for identifying ordinary EBackupUnit responses,
        // but a model NOT appearing here no longer prevents capture.
        // ============================================================

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
                $"{_modelToUnitTypes.Count} unique vehicle models " +
                "(metadata only)."
            );
        }

        // ============================================================
        // B-MENU ACTIVEUNIT DETECTOR
        // ============================================================

        private void CheckForNewBackupUnit()
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

            List<object> activeUnits =
                GetActivePRUnits()
                    .Cast<object>()
                    .Where(
                        unit => unit != null
                    )
                    .ToList();

            foreach (object unit in activeUnits)
            {
                /*
                 * This unit existed before B was pressed.
                 *
                 * It cannot be the newly requested B-menu response.
                 */
                if (_prUnitsBeforeRequest.Contains(
                    unit))
                {
                    continue;
                }

                Vehicle vehicle =
                    GetVehicleFromPRUnit(
                        unit
                    );

                /*
                 * PR may add the BackupUnit to ActiveUnits before the
                 * GTA vehicle has been fully created.
                 */
                if (vehicle == null ||
                    !vehicle.Exists())
                {
                    continue;
                }

                /*
                 * The independent service scanner may already have
                 * claimed a prisoner transport or tow truck.
                 */
                if (_handledServiceVehicles.Contains(
                    vehicle.Handle))
                {
                    continue;
                }

                /*
                 * Wait for PR's spawn/setup sequence to put a driver
                 * into the vehicle.
                 */
                Ped driver =
                    vehicle.Driver;

                if (driver == null ||
                    !driver.Exists())
                {
                    continue;
                }

                // =====================================================
                // PURSUIT FILTER
                //
                // ABSOLUTE EXCLUSION.
                //
                // BetterBackup must never take over pursuit backup.
                // =====================================================

                if (IsCurrentPRRequestPursuit())
                {
                    Game.LogTrivial(
                        "[BetterBackup] Pursuit backup detected. " +
                        $"PRUnit={unit.GetType().FullName}, " +
                        $"Vehicle={vehicle.Model.Name} / {vehicle.Handle}. " +
                        "BetterBackup will not intercept it."
                    );

                    _watchingForBackup =
                        false;

                    return;
                }

                // =====================================================
                // PR DISPATCH CLASSIFICATION
                //
                // Do not intercept until PR has classified this exact
                // unit in DispatchedUnits.
                // =====================================================

                string dispatchType;

                if (!TryGetDispatchTypeForPRUnit(
                    unit,
                    out dispatchType))
                {
                    continue;
                }

                // =====================================================
                // TRAFFIC STOP FILTER
                //
                // ABSOLUTE EXCLUSION.
                //
                // PR already knows how to park traffic-stop backup
                // behind the player's cruiser.
                // =====================================================

                if (string.Equals(
                    dispatchType,
                    "TrafficStop",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Game.LogTrivial(
                        "[BetterBackup] Traffic stop backup detected. " +
                        $"PRUnit={unit.GetType().FullName}, " +
                        $"Vehicle={vehicle.Model.Name} / {vehicle.Handle}. " +
                        "BetterBackup will not intercept it."
                    );

                    _watchingForBackup =
                        false;

                    return;
                }

                Game.LogTrivial(
                    "[BetterBackup] New PR ActiveUnit accepted. " +
                    $"PRUnit={unit.GetType().FullName}, " +
                    $"DispatchType={dispatchType}, " +
                    $"Vehicle={vehicle.Model.Name} / {vehicle.Handle}."
                );

                /*
                 * The vehicle is confirmed as an actual PR unit.
                 *
                 * The model map is only used to provide a built-in
                 * EBackupUnit hint where one exists.
                 */
                List<EBackupUnit> matchingTypes =
                    GetMatchingBuiltInTypes(
                        vehicle
                    );

                HandleCandidate(
                    unit,
                    vehicle,
                    matchingTypes
                );

                return;
            }
        }

        private List<EBackupUnit> GetMatchingBuiltInTypes(
            Vehicle vehicle
        )
        {
            if (vehicle == null ||
                !vehicle.Exists())
            {
                return
                    new List<EBackupUnit>();
            }

            uint modelHash =
                unchecked(
                    (uint)vehicle.Model.Hash
                );

            List<EBackupUnit> matchingTypes;

            if (_modelToUnitTypes.TryGetValue(
                modelHash,
                out matchingTypes))
            {
                return
                    new List<EBackupUnit>(
                        matchingTypes
                    );
            }

            return
                new List<EBackupUnit>();
        }

        // ============================================================
        // NON-B-MENU SERVICE UNIT SCANNER
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

                    Ped driver =
                        vehicle.Driver;

                    if (driver == null ||
                        !driver.Exists())
                    {
                        continue;
                    }

                    /*
                     * If this service unit is also the current B-menu
                     * candidate, let the B-menu path classify it first.
                     *
                     * This prevents the independent scanner racing
                     * ahead of pursuit / TrafficStop exclusions.
                     */
                    if (_watchingForBackup &&
                        !_prUnitsBeforeRequest.Contains(
                            unit))
                    {
                        continue;
                    }

                    /*
                     * Claim it before handing it over.
                     */
                    _handledServiceVehicles.Add(
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
        // CANDIDATE HANDLING
        // ============================================================

        private void HandleCandidate(
            object prUnit,
            Vehicle vehicle,
            List<EBackupUnit> matchingTypes
        )
        {
            _watchingForBackup =
                false;

            /*
             * This PR unit is now ours for this request.
             */
            if (prUnit != null)
            {
                _prUnitsBeforeRequest.Add(
                    prUnit
                );
            }

            /*
             * Ask the cached SpecialUnits.xml metadata reader whether
             * this live PR unit corresponds to a configured SpecialUnit.
             *
             * IMPORTANT:
             *
             * This does NOT classify based on role.
             *
             * It first attempts to identify the configured Special Unit
             * from the live PR unit itself. Vehicle model is only used
             * as a fallback when exactly ONE configured Special Unit
             * uses that model.
             */
            SpecialUnitDefinition specialUnit =
                null;

            bool isSpecialUnit =
                SpecialUnitsReader.TryResolve(
                    prUnit,
                    vehicle,
                    out specialUnit
                );

            string typeText;

            if (isSpecialUnit &&
                specialUnit != null)
            {
                typeText =
                    "SpecialUnit:" +
                    specialUnit.Name;
            }
            else if (matchingTypes != null &&
                     matchingTypes.Count > 0)
            {
                typeText =
                    string.Join(
                        ", ",
                        matchingTypes.Select(
                            type =>
                                type.ToString()
                        )
                    );
            }
            else
            {
                typeText =
                    "<unmapped>";
            }

            Game.LogTrivial(
                "[BetterBackup] Candidate PR backup captured. " +
                $"PRType=" +
                $"{(prUnit != null ? prUnit.GetType().FullName : "<null>")}, " +
                $"Handle={vehicle.Handle}, " +
                $"Model={vehicle.Model.Name}, " +
                $"ResolvedType=[{typeText}], " +
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
            if (matchingTypes != null &&
                matchingTypes.Any(
                    type =>
                        type == EBackupUnit.PoliceTransport ||
                        type == EBackupUnit.SmallTowTruck ||
                        type == EBackupUnit.LargeTowTruck))
            {
                _handledServiceVehicles.Add(
                    vehicle.Handle
                );
            }

            // ========================================================
            // SPECIAL UNIT
            // ========================================================

            if (isSpecialUnit &&
                specialUnit != null)
            {
                Game.LogTrivial(
                    "[BetterBackup] Candidate identified as PR Special Unit: " +
                    $"Name=\"{specialUnit.Name}\", " +
                    $"Role=\"{specialUnit.Role}\", " +
                    $"Vehicle={vehicle.Model.Name}."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "PR backup detected:<br>" +
                    $"~y~{specialUnit.Name}~s~"
                );

                /*
                 * Do NOT manufacture an EBackupUnit from the XML role.
                 *
                 * For example, role=\"medic\" does not necessarily mean
                 * we should pretend this is EBackupUnit.Ambulance.
                 *
                 * PR remains the authority over what this unit actually
                 * is and what it does.
                 */
                _placementController.BeginForBackup(
                    vehicle,
                    null
                );

                return;
            }

            // ========================================================
            // EXACTLY ONE BUILT-IN UNIT TYPE
            // ========================================================

            if (matchingTypes != null &&
                matchingTypes.Count == 1)
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

                return;
            }

            // ========================================================
            // CONFIRMED PR UNIT BUT UNMAPPED
            // ========================================================

            if (matchingTypes == null ||
                matchingTypes.Count == 0)
            {
                Game.LogTrivial(
                    "[BetterBackup] Confirmed PR ActiveUnit could not " +
                    "be matched to SpecialUnits.xml or a unique built-in " +
                    "EBackupUnit type."
                );

                Game.DisplayNotification(
                    "~b~BetterBackup~s~<br>" +
                    "PR backup detected:<br>" +
                    "~y~Unmapped PR unit~s~"
                );

                _placementController.BeginForBackup(
                    vehicle,
                    null
                );

                return;
            }

            // ========================================================
            // MULTIPLE BUILT-IN TYPES
            // ========================================================

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

        // ============================================================
        // REFERENCE IDENTITY COMPARER
        // ============================================================

        private sealed class ReferenceEqualityComparer :
            IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            private ReferenceEqualityComparer()
            {
            }

            public new bool Equals(
                object x,
                object y
            )
            {
                return ReferenceEquals(
                    x,
                    y
                );
            }

            public int GetHashCode(
                object obj
            )
            {
                if (obj == null)
                {
                    return 0;
                }

                return RuntimeHelpers.GetHashCode(
                    obj
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