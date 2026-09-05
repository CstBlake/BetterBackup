using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Rage;

namespace BackupPlacement
{
    internal sealed class SpecialUnitDefinition
    {
        public string Name { get; set; }

        public string Role { get; set; }

        public bool Code1 { get; set; }

        public bool Code2 { get; set; }

        public bool Code3 { get; set; }

        public bool Pursuit { get; set; }

        public bool TrafficStop { get; set; }

        public bool FelonyStop { get; set; }

        public bool HasK9 { get; set; }

        public HashSet<string> VehicleModels { get; set; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
    }

    internal static class SpecialUnitsReader
    {
        private static readonly List<SpecialUnitDefinition>
            _units =
                new List<SpecialUnitDefinition>();

        private static bool _loaded;

        public static IReadOnlyList<SpecialUnitDefinition> Units =>
            _units;

        // ============================================================
        // LOAD ONCE
        // ============================================================

        public static void Load()
        {
            /*
             * Intentionally read SpecialUnits.xml only once during the
             * lifetime of this BetterBackup plugin load.
             *
             * Going off/on duty does NOT reread it.
             */
            if (_loaded)
            {
                return;
            }

            _loaded =
                true;

            _units.Clear();

            string path =
                GetSpecialUnitsPath();

            Game.LogTrivial(
                "[BetterBackup] Loading PR SpecialUnits metadata from: " +
                path
            );

            if (!File.Exists(path))
            {
                Game.LogTrivial(
                    "[BetterBackup] SpecialUnits.xml was not found. " +
                    "Special Unit naming will be unavailable."
                );

                return;
            }

            try
            {
                XDocument document =
                    XDocument.Load(
                        path,
                        LoadOptions.None
                    );

                XElement root =
                    document.Root;

                if (root == null)
                {
                    Game.LogTrivial(
                        "[BetterBackup] SpecialUnits.xml has no root element."
                    );

                    return;
                }

                foreach (
                    XElement element
                    in root.Elements()
                        .Where(
                            e =>
                                string.Equals(
                                    e.Name.LocalName,
                                    "SpecialUnit",
                                    StringComparison.OrdinalIgnoreCase
                                )
                        ))
                {
                    string name =
                        GetAttribute(
                            element,
                            "name"
                        );

                    if (string.IsNullOrWhiteSpace(
                        name))
                    {
                        continue;
                    }

                    SpecialUnitDefinition definition =
                        new SpecialUnitDefinition
                        {
                            Name =
                                name.Trim(),

                            Role =
                                GetAttribute(
                                    element,
                                    "role"
                                ),

                            Code1 =
                                GetBoolAttribute(
                                    element,
                                    "code_1"
                                ),

                            Code2 =
                                GetBoolAttribute(
                                    element,
                                    "code_2"
                                ),

                            Code3 =
                                GetBoolAttribute(
                                    element,
                                    "code_3"
                                ),

                            Pursuit =
                                GetBoolAttribute(
                                    element,
                                    "pursuit"
                                ),

                            TrafficStop =
                                GetBoolAttribute(
                                    element,
                                    "traffic_stop"
                                ),

                            FelonyStop =
                                GetBoolAttribute(
                                    element,
                                    "felony_stop"
                                ),

                            HasK9 =
                                GetBoolAttribute(
                                    element,
                                    "has_k9"
                                )
                        };

                    /*
                     * Collect every vehicle model beneath this
                     * SpecialUnit, regardless of region.
                     *
                     * This is only used as a SAFE fallback.
                     *
                     * We never assume a model identifies a Special Unit
                     * if more than one configured unit uses that model.
                     */
                    foreach (
                        XElement vehicleElement
                        in element.Descendants()
                            .Where(
                                e =>
                                    string.Equals(
                                        e.Name.LocalName,
                                        "Vehicle",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                            ))
                    {
                        string model =
                            vehicleElement.Value;

                        if (string.IsNullOrWhiteSpace(
                            model))
                        {
                            continue;
                        }

                        definition.VehicleModels.Add(
                            model.Trim()
                        );
                    }

                    _units.Add(
                        definition
                    );
                }

                Game.LogTrivial(
                    "[BetterBackup] Loaded " +
                    $"{_units.Count} PR Special Unit definition(s)."
                );

                foreach (
                    SpecialUnitDefinition unit
                    in _units)
                {
                    Game.LogTrivial(
                        "[BetterBackup] SpecialUnit metadata: " +
                        $"Name=\"{unit.Name}\", " +
                        $"Role=\"{unit.Role}\", " +
                        $"Vehicles=[{string.Join(", ", unit.VehicleModels)}]"
                    );
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    "[BetterBackup] Could not read SpecialUnits.xml: " +
                    ex
                );
            }
        }

        // ============================================================
        // RESOLVE LIVE PR UNIT -> SPECIAL UNIT DEFINITION
        // ============================================================

        public static bool TryResolve(
            object prUnit,
            Vehicle vehicle,
            out SpecialUnitDefinition definition
        )
        {
            definition =
                null;

            if (!_loaded)
            {
                Load();
            }

            if (_units.Count == 0 ||
                prUnit == null)
            {
                return false;
            }

            /*
             * FIRST:
             *
             * Inspect the actual live PR unit for the configured name.
             *
             * This is the preferred method because it does not depend
             * on role or vehicle model.
             */
            SpecialUnitDefinition liveObjectMatch =
                TryResolveFromLiveObject(
                    prUnit
                );

            if (liveObjectMatch != null)
            {
                definition =
                    liveObjectMatch;

                Game.LogTrivial(
                    "[BetterBackup] Special Unit resolved from live PR object: " +
                    $"\"{definition.Name}\"."
                );

                return true;
            }

            /*
             * SECOND:
             *
             * Try the PR object's string representation.
             *
             * PR logs units in forms such as:
             *
             * Fuckhead (Medic)-1 (Id: 1)
             *
             * If its live object's ToString() contains that same identity,
             * the configured XML name can be recovered exactly.
             */
            string unitText =
                SafeToString(
                    prUnit
                );

            SpecialUnitDefinition textMatch =
                FindDefinitionInText(
                    unitText
                );

            if (textMatch != null)
            {
                definition =
                    textMatch;

                Game.LogTrivial(
                    "[BetterBackup] Special Unit resolved from PR unit text: " +
                    $"\"{definition.Name}\". " +
                    $"PRText=\"{unitText}\""
                );

                return true;
            }

            /*
             * THIRD:
             *
             * Vehicle model fallback.
             *
             * This is ONLY accepted when exactly one Special Unit in
             * SpecialUnits.xml uses the model.
             *
             * If Medic and Fire both use FBI2, no guess is made.
             */
            if (vehicle != null &&
                vehicle.Exists())
            {
                string modelName =
                    vehicle.Model.Name;

                List<SpecialUnitDefinition> modelMatches =
                    _units
                        .Where(
                            u =>
                                u.VehicleModels.Contains(
                                    modelName
                                )
                        )
                        .ToList();

                if (modelMatches.Count == 1)
                {
                    definition =
                        modelMatches[0];

                    Game.LogTrivial(
                        "[BetterBackup] Special Unit resolved by unique " +
                        $"vehicle-model fallback: \"{definition.Name}\" " +
                        $"(Model={modelName})."
                    );

                    return true;
                }

                if (modelMatches.Count > 1)
                {
                    Game.LogTrivial(
                        "[BetterBackup] Special Unit vehicle-model fallback " +
                        $"is ambiguous. Model={modelName}, " +
                        $"Matches=[{string.Join(", ", modelMatches.Select(x => x.Name))}]."
                    );
                }
            }

            return false;
        }

        // ============================================================
        // LIVE OBJECT INSPECTION
        // ============================================================

        private static SpecialUnitDefinition TryResolveFromLiveObject(
            object prUnit
        )
        {
            Type type =
                prUnit.GetType();

            /*
             * Inspect strings exposed by PR's unit object.
             *
             * We are NOT looking for any hardcoded property such as
             * "SpecialUnitName".
             *
             * Instead, every readable string field/property is tested
             * against the configured XML names.
             */
            Type current =
                type;

            while (current != null)
            {
                PropertyInfo[] properties;

                try
                {
                    properties =
                        current.GetProperties(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );
                }
                catch
                {
                    properties =
                        Array.Empty<PropertyInfo>();
                }

                foreach (
                    PropertyInfo property
                    in properties)
                {
                    if (property == null ||
                        property.PropertyType != typeof(string) ||
                        !property.CanRead ||
                        property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    try
                    {
                        MethodInfo getter =
                            property.GetGetMethod(
                                true
                            );

                        if (getter == null)
                        {
                            continue;
                        }

                        string value =
                            getter.Invoke(
                                prUnit,
                                null
                            ) as string;

                        SpecialUnitDefinition match =
                            FindDefinitionInText(
                                value
                            );

                        if (match != null)
                        {
                            Game.LogTrivial(
                                "[BetterBackup] Special Unit name matched " +
                                $"PR property {current.FullName}.{property.Name}."
                            );

                            return match;
                        }
                    }
                    catch
                    {
                    }
                }

                FieldInfo[] fields;

                try
                {
                    fields =
                        current.GetFields(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );
                }
                catch
                {
                    fields =
                        Array.Empty<FieldInfo>();
                }

                foreach (
                    FieldInfo field
                    in fields)
                {
                    if (field == null ||
                        field.FieldType != typeof(string))
                    {
                        continue;
                    }

                    try
                    {
                        string value =
                            field.GetValue(
                                prUnit
                            ) as string;

                        SpecialUnitDefinition match =
                            FindDefinitionInText(
                                value
                            );

                        if (match != null)
                        {
                            Game.LogTrivial(
                                "[BetterBackup] Special Unit name matched " +
                                $"PR field {current.FullName}.{field.Name}."
                            );

                            return match;
                        }
                    }
                    catch
                    {
                    }
                }

                current =
                    current.BaseType;
            }

            return null;
        }

        private static SpecialUnitDefinition FindDefinitionInText(
            string value
        )
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return null;
            }

            string text =
                value.Trim();

            /*
             * Longest names first.
             *
             * Prevents a shorter configured name from accidentally
             * winning when one Special Unit's name is a prefix of
             * another.
             */
            foreach (
                SpecialUnitDefinition definition
                in _units
                    .OrderByDescending(
                        u =>
                            u.Name.Length
                    ))
            {
                if (string.Equals(
                    text,
                    definition.Name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }

                /*
                 * PR commonly appends an instance suffix:
                 *
                 * Supervisor-1
                 * Fuckhead (Medic)-1
                 *
                 * or additional diagnostic text.
                 */
                if (text.StartsWith(
                    definition.Name + "-",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }

                if (text.StartsWith(
                    definition.Name + " ",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }

                if (text.IndexOf(
                    definition.Name,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return definition;
                }
            }

            return null;
        }

        // ============================================================
        // XML HELPERS
        // ============================================================

        private static string GetAttribute(
            XElement element,
            string name
        )
        {
            XAttribute attribute =
                element.Attributes()
                    .FirstOrDefault(
                        a =>
                            string.Equals(
                                a.Name.LocalName,
                                name,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

            return
                attribute?.Value;
        }

        private static bool GetBoolAttribute(
            XElement element,
            string name
        )
        {
            string value =
                GetAttribute(
                    element,
                    name
                );

            if (string.IsNullOrWhiteSpace(
                value))
            {
                return false;
            }

            bool result;

            if (bool.TryParse(
                value,
                out result))
            {
                return result;
            }

            return false;
        }

        // ============================================================
        // PATH
        // ============================================================

        private static string GetSpecialUnitsPath()
        {
            string baseDirectory =
                AppDomain.CurrentDomain.BaseDirectory;

            string path =
                Path.Combine(
                    baseDirectory,
                    "plugins",
                    "LSPDFR",
                    "PolicingRedefined",
                    "Backup",
                    "SpecialUnits.xml"
                );

            /*
             * RPH normally has AppDomain.BaseDirectory at the GTA root.
             *
             * Keep a CurrentDirectory fallback in case a user's RPH
             * environment differs.
             */
            if (File.Exists(
                path))
            {
                return path;
            }

            return Path.Combine(
                Environment.CurrentDirectory,
                "plugins",
                "LSPDFR",
                "PolicingRedefined",
                "Backup",
                "SpecialUnits.xml"
            );
        }

        private static string SafeToString(
            object value
        )
        {
            if (value == null)
            {
                return null;
            }

            try
            {
                return value.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}